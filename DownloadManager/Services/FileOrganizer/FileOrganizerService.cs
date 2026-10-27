using System.Text.RegularExpressions;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Download;
using DownloadManager.Services.Sftp;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.FileOrganizer;

public class FileOrganizerService : IFileOrganizerService
{
    private readonly ILogger<FileOrganizerService> _logger;
    private readonly FileOperationRepository _repository;
    private readonly ISftpService _sftpService;
    private ISftpService? _remoteOverride;

    /// <summary>Server su cui lavorano le operazioni remote: quello scelto, altrimenti la connessione principale.</summary>
    private ISftpService Remote => _remoteOverride ?? _sftpService;

    public void UseRemoteServer(ISftpService? service) => _remoteOverride = service;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".m4v"
    };

    /// <summary>File video considerato da riordina/unisci/dividi.</summary>
    public static bool IsVideoFile(string fileName) => VideoExtensions.Contains(Path.GetExtension(fileName));

    private static readonly Regex EpisodeMarkerRegex = new(
        @"(?:^|[\s_\-\(\[\._])(?:Ep|Episode|E|Puntata|P)[\s_\-\.]*(\d{1,4}(?:[\-\._]\d{1,4})*)(?:[\s_\-\.\)\]]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IsolatedNumberRegex = new(
        @"(?:^|[\s_\-\.\(\[_])(\d{1,4}(?:[\-\._]\d{1,4})*)(?:[\s_\-\.\)\]_]|$)",
        RegexOptions.Compiled);

    private static readonly Regex FirstNumberRegex = new(@"(\d{1,4})", RegexOptions.Compiled);


    private static readonly Regex TitleRegex = new(
        @"(?:Episodio|Ep|Episode|Puntata)\s*\d+(?:[\-\._]\d+)*\s*[-–]?\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CleanSeriesSuffixRegex = new(
        @"[\s_\-\.]*(SUB|ITA|DUB|MULTI|1080p|720p|480p|HD|SD|BDRip|WEB[-_]?DL|WEBRip|H264|H265|HEVC|x264|x265).*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CollapseWhitespaceRegex = new(@"[\s_\-\.]+", RegexOptions.Compiled);
    private static readonly Regex CamelCaseSplitRegex = new(@"(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);

    private static readonly Regex SplitFolderNumberRegex = new(@"\{Number(?::(D\d+))?\}", RegexOptions.Compiled);
    private static readonly Regex SplitFolderStartRegex = new(@"\{Start(?::(D\d+))?\}", RegexOptions.Compiled);
    private static readonly Regex SplitFolderEndRegex = new(@"\{End(?::(D\d+))?\}", RegexOptions.Compiled);
    private static readonly Regex EpisodeRangeRegex = new(@"\{Episode(?::(D\d+))?\}", RegexOptions.Compiled);

    public FileOrganizerService(
        ILogger<FileOrganizerService> logger,
        FileOperationRepository repository,
        ISftpService sftpService)
    {
        _logger = logger;
        _repository = repository;
        _sftpService = sftpService;
    }

    private record EpisodeInfo(int Start, int End, int Count, string RawRange, bool HasRange);

    // ============================================================
    //  PREVIEW — LOCALE, SINGOLA CARTELLA
    // ============================================================

    public Task<List<ProposedOperation>> PreviewAsync(
        string folder, string pattern, bool createSubfolders,
        int startingEpisode = 1, CancellationToken ct = default)
        => Task.Run(() => PreviewInternal(folder, pattern, createSubfolders, startingEpisode, ct), ct);

    private List<ProposedOperation> PreviewInternal(
        string folder, string pattern, bool createSubfolders,
        int startingEpisode, CancellationToken ct)
    {
        var result = new List<ProposedOperation>();

        if (!Directory.Exists(folder))
        {
            _logger.LogWarning("Cartella non trovata: {Folder}", folder);
            return result;
        }

        if (startingEpisode < 1) startingEpisode = 1;

        // Precalcolo info + ordinamento per numero episodio (poi per nome).
        // Necessario per la rinumerazione sequenziale: primo file → startingEpisode,
        // secondo → startingEpisode+1, ecc.
        var files = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .Select(f => new
            {
                Path = f,
                Info = ExtractEpisodeInfo(Path.GetFileNameWithoutExtension(f))
            })
            .OrderBy(x => x.Info.Start)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Trovati {Count} file video in {Folder}", files.Count, folder);

        var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        int currentEpisode = startingEpisode;

        foreach (var entry in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var fileName = Path.GetFileNameWithoutExtension(entry.Path);
                var ext = Path.GetExtension(entry.Path).TrimStart('.');

                var series = ExtractSeriesName(fileName);
                if (string.IsNullOrWhiteSpace(series)) series = folderName;

                int count = Math.Max(1, entry.Info.Count);
                var info = new EpisodeInfo(
                    currentEpisode,
                    currentEpisode + count - 1,
                    count,
                    count > 1
                        ? string.Join("-", Enumerable.Range(currentEpisode, count))
                        : currentEpisode.ToString(),
                    count > 1);

                currentEpisode += count;

                var title = ExtractTitle(fileName);
                var newName = ApplyPatternWithRange(pattern, series, info, title, fileName);

                var destFolder = folder;
                if (createSubfolders && !string.IsNullOrWhiteSpace(series))
                    destFolder = Path.Combine(folder, Sanitize(series));

                var newPath = Path.Combine(destFolder, SanitizeRelative(newName, Path.DirectorySeparatorChar) + "." + ext);

                if (string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                var op = new ProposedOperation
                {
                    OriginalPath = entry.Path,
                    NewPath = newPath,
                    SourceFolder = folder,
                    Type = string.Equals(Path.GetDirectoryName(entry.Path), destFolder, StringComparison.OrdinalIgnoreCase)
                        ? FileOperationType.Rename
                        : FileOperationType.Move
                };

                if (File.Exists(newPath))
                {
                    op.HasConflict = true;
                    op.ConflictReason = "File di destinazione già esistente";
                }

                result.Add(op);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore analisi file {File}", entry.Path);
            }
        }

        ResolveRenameChains(result);
        return result;
    }

    // ============================================================
    //  PREVIEW — LOCALE, MERGE
    // ============================================================

    public Task<List<ProposedOperation>> PreviewMergeAsync(
        List<MergeSourceFolder> sourceFolders, string destinationFolder,
        string pattern, bool createSubfolders,
        int startingEpisode = 1, CancellationToken ct = default)
        => Task.Run(() => PreviewMergeInternal(sourceFolders, destinationFolder, pattern,
            createSubfolders, startingEpisode, ct), ct);

    private List<ProposedOperation> PreviewMergeInternal(
        List<MergeSourceFolder> sourceFolders, string destinationFolder,
        string pattern, bool createSubfolders, int startingEpisode, CancellationToken ct)
    {
        var result = new List<ProposedOperation>();
        if (sourceFolders.Count == 0) return result;
        if (startingEpisode < 1) startingEpisode = 1;

        var ordered = sourceFolders.OrderBy(f => f.Priority).ToList();

        // Il counter parte da startingEpisode - 1, così il primo file
        // (newStart = counter + 1) avrà proprio startingEpisode.
        int globalEpisodeCounter = startingEpisode - 1;

        foreach (var source in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(source.Path)) continue;

            var folderName = Path.GetFileName(source.Path.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var files = Directory.GetFiles(source.Path, "*", SearchOption.TopDirectoryOnly)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
                .Select(f => new
                {
                    FilePath = f,
                    Info = ExtractEpisodeInfo(Path.GetFileNameWithoutExtension(f)),
                    FileName = Path.GetFileName(f)
                })
                .OrderBy(x => x.Info.Start)
                .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            source.FileCount = files.Count;

            foreach (var entry in files)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var file = entry.FilePath;
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var ext = Path.GetExtension(file).TrimStart('.');
                    var info = entry.Info;
                    int count = Math.Max(1, info.Count);

                    int newStart = globalEpisodeCounter + 1;
                    int newEnd = newStart + count - 1;
                    globalEpisodeCounter += count;

                    var newInfo = new EpisodeInfo(newStart, newEnd, count,
                        count > 1 ? string.Join("-", Enumerable.Range(newStart, count)) : newStart.ToString(),
                        count > 1);

                    var series = ExtractSeriesName(fileName);
                    if (string.IsNullOrWhiteSpace(series)) series = folderName;

                    var title = ExtractTitle(fileName);
                    var newName = ApplyPatternWithRange(pattern, series, newInfo, title, fileName);

                    var destFolder = destinationFolder;
                    if (createSubfolders && !string.IsNullOrWhiteSpace(series))
                        destFolder = Path.Combine(destinationFolder, Sanitize(series));

                    var newPath = Path.Combine(destFolder, SanitizeRelative(newName, Path.DirectorySeparatorChar) + "." + ext);

                    if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
                        continue;

                    var op = new ProposedOperation
                    {
                        OriginalPath = file,
                        NewPath = newPath,
                        SourceFolder = source.Path,
                        Type = FileOperationType.Move
                    };

                    if (File.Exists(newPath))
                    {
                        op.HasConflict = true;
                        op.ConflictReason = "File di destinazione già esistente";
                    }

                    result.Add(op);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Errore analisi file {File}", entry.FilePath);
                }
            }
        }

        ResolveRenameChains(result);
        return result;
    }

    // ============================================================
    //  PREVIEW — LOCALE, SPLIT
    // ============================================================

    public Task<List<ProposedOperation>> PreviewSplitAsync(
        string sourceFolder, string destinationFolder, List<int> segmentSizes,
        string filePattern, string folderPattern, SplitNumberingMode mode,
        CancellationToken ct = default)
        => Task.Run(() => PreviewSplitInternal(sourceFolder, destinationFolder, segmentSizes,
            filePattern, folderPattern, mode, ct), ct);

    private List<ProposedOperation> PreviewSplitInternal(
        string sourceFolder, string destinationFolder, List<int> segmentSizes,
        string filePattern, string folderPattern, SplitNumberingMode mode, CancellationToken ct)
    {
        var result = new List<ProposedOperation>();

        if (!Directory.Exists(sourceFolder)) return result;
        if (segmentSizes == null || segmentSizes.Count == 0) return result;
        if (string.IsNullOrWhiteSpace(folderPattern)) folderPattern = "{Number}";

        var files = Directory.GetFiles(sourceFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .Select(f => new
            {
                FilePath = f,
                Info = ExtractEpisodeInfo(Path.GetFileNameWithoutExtension(f)),
                FileName = Path.GetFileName(f)
            })
            .OrderBy(x => x.Info.Start)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0) return result;

        int totalEpisodes = files.Sum(f => Math.Max(1, f.Info.Count));
        var finalSegments = new List<int>(segmentSizes.Where(s => s > 0));
        if (finalSegments.Count == 0) return result;

        int userSum = finalSegments.Sum();
        if (userSum < totalEpisodes)
            finalSegments.Add(totalEpisodes - userSum);

        var folderName = Path.GetFileName(sourceFolder.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        int globalCounter = 0;
        int fileIndex = 0;

        for (int segIdx = 0; segIdx < finalSegments.Count && fileIndex < files.Count; segIdx++)
        {
            ct.ThrowIfCancellationRequested();

            int targetEpisodes = finalSegments[segIdx];
            int segmentStart = fileIndex;
            int accumulated = 0;

            while (fileIndex < files.Count)
            {
                var info = files[fileIndex].Info;
                int count = Math.Max(1, info.Count);
                if (accumulated + count > targetEpisodes && accumulated > 0) break;
                accumulated += count;
                fileIndex++;
            }

            if (accumulated == 0) break;

            int segNum = segIdx + 1;
            int rangeStart = mode == SplitNumberingMode.Reset ? 1 : globalCounter + 1;
            int rangeEnd = mode == SplitNumberingMode.Reset ? accumulated : globalCounter + accumulated;

            var segSeries = SeriesOrFolder(Path.GetFileNameWithoutExtension(files[segmentStart].FilePath), folderName);
            var folderNameBuilt = BuildSplitFolderName(folderPattern, segNum, rangeStart, rangeEnd, segSeries);
            // "/" crea più livelli di sottocartelle (es. "{Series}/Parte {Number}")
            var subFolder = Path.Combine(destinationFolder, SanitizeRelative(folderNameBuilt, Path.DirectorySeparatorChar));

            int localCounter = 0;

            for (int i = segmentStart; i < fileIndex; i++)
            {
                var entry = files[i];
                var info = entry.Info;
                int count = Math.Max(1, info.Count);

                int newStart, newEnd;
                if (mode == SplitNumberingMode.Reset)
                {
                    newStart = localCounter + 1;
                    newEnd = newStart + count - 1;
                    localCounter += count;
                }
                else
                {
                    newStart = globalCounter + 1;
                    newEnd = newStart + count - 1;
                    globalCounter += count;
                }

                var newInfo = new EpisodeInfo(newStart, newEnd, count,
                    count > 1 ? string.Join("-", Enumerable.Range(newStart, count)) : newStart.ToString(),
                    count > 1);

                var fileName = Path.GetFileNameWithoutExtension(entry.FilePath);
                var ext = Path.GetExtension(entry.FilePath).TrimStart('.');

                var series = ExtractSeriesName(fileName);
                if (string.IsNullOrWhiteSpace(series)) series = folderName;

                var title = ExtractTitle(fileName);
                var newName = ApplyPatternWithRange(filePattern, series, newInfo, title, fileName);
                var newPath = Path.Combine(subFolder, SanitizeRelative(newName, Path.DirectorySeparatorChar) + "." + ext);

                if (string.Equals(Path.GetFullPath(entry.FilePath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                var op = new ProposedOperation
                {
                    OriginalPath = entry.FilePath,
                    NewPath = newPath,
                    SourceFolder = sourceFolder,
                    Type = FileOperationType.Move
                };

                if (File.Exists(newPath))
                {
                    op.HasConflict = true;
                    op.ConflictReason = "File di destinazione già esistente";
                }

                result.Add(op);
            }

            if (mode == SplitNumberingMode.Reset) globalCounter += accumulated;
        }

        ResolveRenameChains(result);
        return result;
    }

    // ============================================================
    //  PREVIEW — REMOTO, SINGOLA CARTELLA
    // ============================================================

    public async Task<List<ProposedOperation>> PreviewRemoteFolderAsync(
        string remoteFolder, string pattern, bool createSubfolders,
        int startingEpisode = 1, CancellationToken ct = default)
    {
        var result = new List<ProposedOperation>();

        if (string.IsNullOrWhiteSpace(remoteFolder)) return result;
        if (!Remote.IsConnected)
            throw new InvalidOperationException("Non connesso al server SFTP.");
        if (startingEpisode < 1) startingEpisode = 1;

        var remoteBase = remoteFolder.TrimEnd('/');
        if (string.IsNullOrEmpty(remoteBase)) remoteBase = "/";

        var entries = await Remote.ListDirectoryAsync(remoteBase, ct);

        // Precalcolo info + ordinamento per numero episodio (poi per nome).
        var files = entries
            .Where(e => !e.IsDirectory && VideoExtensions.Contains(Path.GetExtension(e.Name)))
            .Select(e => new
            {
                Entry = e,
                Info = ExtractEpisodeInfo(Path.GetFileNameWithoutExtension(e.Name))
            })
            .OrderBy(x => x.Info.Start)
            .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Remoto: {Count} file video in {Folder}", files.Count, remoteBase);

        var folderName = RemoteFileName(remoteBase);
        if (string.IsNullOrEmpty(folderName)) folderName = "root";

        var listedDirs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [remoteBase] = new HashSet<string>(
                entries.Where(e => !e.IsDirectory).Select(e => e.Name),
                StringComparer.OrdinalIgnoreCase)
        };

        var assignedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int currentEpisode = startingEpisode;

        foreach (var item in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var entry = item.Entry;
                var fileName = Path.GetFileNameWithoutExtension(entry.Name);
                var ext = Path.GetExtension(entry.Name).TrimStart('.');

                var series = ExtractSeriesName(fileName);
                if (string.IsNullOrWhiteSpace(series)) series = folderName;

                int count = Math.Max(1, item.Info.Count);
                var info = new EpisodeInfo(
                    currentEpisode,
                    currentEpisode + count - 1,
                    count,
                    count > 1
                        ? string.Join("-", Enumerable.Range(currentEpisode, count))
                        : currentEpisode.ToString(),
                    count > 1);

                currentEpisode += count;

                var title = ExtractTitle(fileName);
                var newName = SanitizeRelative(ApplyPatternWithRange(pattern, series, info, title, fileName), '/') + "." + ext;

                var destFolder = remoteBase;
                if (createSubfolders && !string.IsNullOrWhiteSpace(series))
                    destFolder = remoteBase.TrimEnd('/') + "/" + Sanitize(series);

                var newPath = destFolder.TrimEnd('/') + "/" + newName;

                if (string.Equals(entry.FullPath, newPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var op = new ProposedOperation
                {
                    OriginalPath = entry.FullPath,
                    NewPath = newPath,
                    SourceFolder = remoteBase,
                    Type = string.Equals(destFolder, remoteBase, StringComparison.OrdinalIgnoreCase)
                        ? FileOperationType.Rename
                        : FileOperationType.Move
                };

                await CheckRemoteConflictAsync(op, RemoteDirName(newPath), RemoteFileName(newPath), listedDirs, assignedTargets, ct, entry.FullPath);
                result.Add(op);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore analisi file remoto {File}", item.Entry.FullPath);
            }
        }

        ResolveRenameChains(result);
        return result;
    }

    // ============================================================
    //  PREVIEW — REMOTO, MERGE
    // ============================================================

    public async Task<List<ProposedOperation>> PreviewRemoteMergeAsync(
        List<MergeSourceFolder> sourceFolders, string remoteDestinationFolder,
        string pattern, bool createSubfolders,
        int startingEpisode = 1, CancellationToken ct = default)
    {
        var result = new List<ProposedOperation>();
        if (sourceFolders.Count == 0) return result;
        if (!Remote.IsConnected)
            throw new InvalidOperationException("Non connesso al server SFTP.");
        if (startingEpisode < 1) startingEpisode = 1;

        var remoteDest = remoteDestinationFolder.TrimEnd('/');
        if (string.IsNullOrEmpty(remoteDest)) remoteDest = "/";

        var ordered = sourceFolders.OrderBy(f => f.Priority).ToList();

        // Il counter parte da startingEpisode - 1, così il primo file
        // avrà proprio startingEpisode.
        int globalEpisodeCounter = startingEpisode - 1;

        var listedDirs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var assignedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in ordered)
        {
            ct.ThrowIfCancellationRequested();

            var sourcePath = source.Path.TrimEnd('/');
            if (string.IsNullOrEmpty(sourcePath)) continue;

            List<SftpRemoteEntry> entries;
            try
            {
                entries = await Remote.ListDirectoryAsync(sourcePath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Impossibile listare sorgente remota {Path}", sourcePath);
                continue;
            }

            var folderName = RemoteFileName(sourcePath);
            if (string.IsNullOrEmpty(folderName)) folderName = "root";

            var files = entries
                .Where(e => !e.IsDirectory && VideoExtensions.Contains(Path.GetExtension(e.Name)))
                .Select(e => new
                {
                    Entry = e,
                    Info = ExtractEpisodeInfo(Path.GetFileNameWithoutExtension(e.Name))
                })
                .OrderBy(x => x.Info.Start)
                .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            source.FileCount = files.Count;

            foreach (var item in files)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var entry = item.Entry;
                    var fileName = Path.GetFileNameWithoutExtension(entry.Name);
                    var ext = Path.GetExtension(entry.Name).TrimStart('.');
                    var info = item.Info;
                    int count = Math.Max(1, info.Count);

                    int newStart = globalEpisodeCounter + 1;
                    int newEnd = newStart + count - 1;
                    globalEpisodeCounter += count;

                    var newInfo = new EpisodeInfo(newStart, newEnd, count,
                        count > 1 ? string.Join("-", Enumerable.Range(newStart, count)) : newStart.ToString(),
                        count > 1);

                    var series = ExtractSeriesName(fileName);
                    if (string.IsNullOrWhiteSpace(series)) series = folderName;

                    var title = ExtractTitle(fileName);
                    var newName = SanitizeRelative(ApplyPatternWithRange(pattern, series, newInfo, title, fileName), '/') + "." + ext;

                    var destFolder = remoteDest;
                    if (createSubfolders && !string.IsNullOrWhiteSpace(series))
                        destFolder = remoteDest.TrimEnd('/') + "/" + Sanitize(series);

                    var newPath = destFolder.TrimEnd('/') + "/" + newName;

                    if (string.Equals(entry.FullPath, newPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var op = new ProposedOperation
                    {
                        OriginalPath = entry.FullPath,
                        NewPath = newPath,
                        SourceFolder = sourcePath,
                        Type = FileOperationType.Move
                    };

                    await CheckRemoteConflictAsync(op, RemoteDirName(newPath), RemoteFileName(newPath), listedDirs, assignedTargets, ct, entry.FullPath);
                    result.Add(op);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Errore analisi file remoto {File}", item.Entry.FullPath);
                }
            }
        }

        ResolveRenameChains(result);
        return result;
    }

    // ============================================================
    //  PREVIEW — REMOTO, SPLIT
    // ============================================================

    public async Task<List<ProposedOperation>> PreviewRemoteSplitAsync(
        string remoteSourceFolder, string remoteDestinationFolder,
        List<int> segmentSizes, string filePattern, string folderPattern,
        SplitNumberingMode mode, CancellationToken ct = default)
    {
        var result = new List<ProposedOperation>();
        if (!Remote.IsConnected)
            throw new InvalidOperationException("Non connesso al server SFTP.");
        if (segmentSizes == null || segmentSizes.Count == 0) return result;
        if (string.IsNullOrWhiteSpace(folderPattern)) folderPattern = "{Number}";

        var sourceBase = remoteSourceFolder.TrimEnd('/');
        if (string.IsNullOrEmpty(sourceBase)) sourceBase = "/";

        var destBase = remoteDestinationFolder.TrimEnd('/');
        if (string.IsNullOrEmpty(destBase)) destBase = sourceBase;

        var entries = await Remote.ListDirectoryAsync(sourceBase, ct);
        var files = entries
            .Where(e => !e.IsDirectory && VideoExtensions.Contains(Path.GetExtension(e.Name)))
            .Select(e => new
            {
                Entry = e,
                Info = ExtractEpisodeInfo(Path.GetFileNameWithoutExtension(e.Name))
            })
            .OrderBy(x => x.Info.Start)
            .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0) return result;

        int totalEpisodes = files.Sum(f => Math.Max(1, f.Info.Count));
        var finalSegments = new List<int>(segmentSizes.Where(s => s > 0));
        if (finalSegments.Count == 0) return result;

        int userSum = finalSegments.Sum();
        if (userSum < totalEpisodes)
            finalSegments.Add(totalEpisodes - userSum);

        var folderName = RemoteFileName(sourceBase);
        if (string.IsNullOrEmpty(folderName)) folderName = "root";

        var listedDirs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var assignedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int globalCounter = 0;
        int fileIndex = 0;

        for (int segIdx = 0; segIdx < finalSegments.Count && fileIndex < files.Count; segIdx++)
        {
            ct.ThrowIfCancellationRequested();

            int targetEpisodes = finalSegments[segIdx];
            int segmentStart = fileIndex;
            int accumulated = 0;

            while (fileIndex < files.Count)
            {
                var info = files[fileIndex].Info;
                int count = Math.Max(1, info.Count);
                if (accumulated + count > targetEpisodes && accumulated > 0) break;
                accumulated += count;
                fileIndex++;
            }

            if (accumulated == 0) break;

            int segNum = segIdx + 1;
            int rangeStart = mode == SplitNumberingMode.Reset ? 1 : globalCounter + 1;
            int rangeEnd = mode == SplitNumberingMode.Reset ? accumulated : globalCounter + accumulated;

            var segSeries = SeriesOrFolder(Path.GetFileNameWithoutExtension(files[segmentStart].Entry.Name), folderName);
            var folderNameBuilt = BuildSplitFolderName(folderPattern, segNum, rangeStart, rangeEnd, segSeries);
            // "/" crea più livelli di sottocartelle (es. "{Series}/Parte {Number}")
            var subFolder = destBase.TrimEnd('/') + "/" + SanitizeRelative(folderNameBuilt, '/');

            int localCounter = 0;

            for (int i = segmentStart; i < fileIndex; i++)
            {
                var item = files[i];
                var entry = item.Entry;
                var info = item.Info;
                int count = Math.Max(1, info.Count);

                int newStart, newEnd;
                if (mode == SplitNumberingMode.Reset)
                {
                    newStart = localCounter + 1;
                    newEnd = newStart + count - 1;
                    localCounter += count;
                }
                else
                {
                    newStart = globalCounter + 1;
                    newEnd = newStart + count - 1;
                    globalCounter += count;
                }

                var newInfo = new EpisodeInfo(newStart, newEnd, count,
                    count > 1 ? string.Join("-", Enumerable.Range(newStart, count)) : newStart.ToString(),
                    count > 1);

                var fileName = Path.GetFileNameWithoutExtension(entry.Name);
                var ext = Path.GetExtension(entry.Name).TrimStart('.');

                var series = ExtractSeriesName(fileName);
                if (string.IsNullOrWhiteSpace(series)) series = folderName;

                var title = ExtractTitle(fileName);
                var newName = SanitizeRelative(ApplyPatternWithRange(filePattern, series, newInfo, title, fileName), '/') + "." + ext;
                var newPath = subFolder.TrimEnd('/') + "/" + newName;

                if (string.Equals(entry.FullPath, newPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var op = new ProposedOperation
                {
                    OriginalPath = entry.FullPath,
                    NewPath = newPath,
                    SourceFolder = sourceBase,
                    Type = FileOperationType.Move
                };

                await CheckRemoteConflictAsync(op, RemoteDirName(newPath), RemoteFileName(newPath), listedDirs, assignedTargets, ct, entry.FullPath);
                result.Add(op);
            }

            if (mode == SplitNumberingMode.Reset) globalCounter += accumulated;
        }

        ResolveRenameChains(result);
        return result;
    }

    // ============================================================
    //  CONFLICT CHECK (remoto)
    // ============================================================

    private async Task CheckRemoteConflictAsync(
        ProposedOperation op, string destFolder, string newName,
        Dictionary<string, HashSet<string>> listedDirs,
        HashSet<string> assignedTargets,
        CancellationToken ct,
        string originalPath)
    {
        if (!listedDirs.TryGetValue(destFolder, out var existingNames))
        {
            try
            {
                var destEntries = await Remote.ListDirectoryAsync(destFolder, ct);
                existingNames = new HashSet<string>(
                    destEntries.Where(e => !e.IsDirectory).Select(e => e.Name),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            listedDirs[destFolder] = existingNames;
        }

        var newPath = op.NewPath;
        if (existingNames.Contains(newName) &&
            !string.Equals(originalPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            op.HasConflict = true;
            op.ConflictReason = "File di destinazione già esistente";
        }
        else if (!assignedTargets.Add(newPath))
        {
            op.HasConflict = true;
            op.ConflictReason = "Destinazione duplicata nel batch";
        }
        else
        {
            existingNames.Add(newName);
        }
    }

    // ============================================================
    //  CATENE DI RINOMINE (es. Ep_03 → Ep_02, Ep_04 → Ep_03, ...)
    // ============================================================

    private const string ExistsReason = "File di destinazione già esistente";

    /// <summary>
    /// Una destinazione "già esistente" non è un conflitto se quel file è a sua volta rinominato
    /// nello stesso gruppo (es. dopo aver eliminato l'episodio 2, il 3 diventa 2, il 4 diventa 3...).
    /// Si ripete finché cambia qualcosa: un file libera il suo nome solo se la sua rinomina va.
    /// Due file verso lo stesso nome restano un conflitto.
    /// </summary>
    private static void ResolveRenameChains(List<ProposedOperation> ops)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        // stessa destinazione per più file: conflitto vero
        foreach (var g in ops.Where(o => !o.HasConflict).GroupBy(o => o.NewPath, cmp).Where(g => g.Count() > 1))
            foreach (var o in g.Skip(1)) { o.HasConflict = true; o.ConflictReason = "Destinazione duplicata nel batch"; }

        // Si parte supponendo che vadano tutte le rinomine bloccate solo da "già esistente" (così
        // funzionano anche gli scambi A ↔ B), poi si tolgono quelle la cui destinazione resta
        // davvero occupata: un file che non si sposta, o che è bloccato per un altro motivo.
        var candidates = ops.Where(o => !o.IsDuplicate && (!o.HasConflict || o.ConflictReason == ExistsReason)).ToHashSet();
        bool changed = true;
        while (changed)
        {
            changed = false;
            var moving = candidates.Select(o => o.OriginalPath).ToHashSet(cmp);
            foreach (var o in candidates.ToList())
            {
                if (o.HasConflict && !moving.Contains(o.NewPath)) { candidates.Remove(o); changed = true; }
            }
        }
        foreach (var o in candidates.Where(o => o.HasConflict))
        {
            // stessa destinazione di un'altra rinomina che va: resta un conflitto
            if (ops.Any(x => !ReferenceEquals(x, o) && candidates.Contains(x) && cmp.Equals(x.NewPath, o.NewPath))) continue;
            o.HasConflict = false;
            o.ConflictReason = string.Empty;
        }
    }

    /// <summary>Nome temporaneo nella stessa cartella del file, per le rinomine in due passaggi.</summary>
    private static string TempName(string path, char sep, string batchId, int i)
    {
        var idx = path.LastIndexOfAny(new[] { '/', '\\' });
        var dir = idx >= 0 ? path[..idx] : "";
        var name = $".ddlorg-{batchId[..8]}-{i}.tmp";
        return dir.Length > 0 ? dir + sep + name : name;
    }

    // ============================================================
    //  APPLY — LOCALE
    // ============================================================

    public async Task<int> ApplyAsync(List<ProposedOperation> operations, CancellationToken ct = default)
    {
        var applied = 0;
        var batchId = Guid.NewGuid().ToString("N");
        var log = new List<FileOperation>();

        // Catena di rinomine (una destinazione è il file di un'altra rinomina, o due file si
        // scambiano il nome): prima tutti su un nome temporaneo, poi ognuno sul nome finale.
        var todo = operations.Where(o => !o.IsDuplicate && !o.HasConflict).ToList();
        var sources = todo.Select(o => o.OriginalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = new Dictionary<ProposedOperation, string>();
        if (todo.Any(o => sources.Contains(o.NewPath)))
        {
            int i = 0;
            foreach (var op in todo)
            {
                if (!File.Exists(op.OriginalPath)) continue;
                var tmp = TempName(op.OriginalPath, Path.DirectorySeparatorChar, batchId, i++);
                try { File.Move(op.OriginalPath, tmp); staged[op] = tmp; }
                catch (Exception ex) { _logger.LogError(ex, "Rinomina temporanea fallita: {File}", op.OriginalPath); }
            }
        }

        try
        {
            foreach (var op in operations)
            {
                ct.ThrowIfCancellationRequested();
                if (op.IsDuplicate || op.HasConflict) continue;

                try
                {
                    var destDir = Path.GetDirectoryName(op.NewPath);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    {
                        // Con "{Series}/S01/..." mancano più livelli: si registrano tutti, dal più esterno
                        var missing = new List<string>();
                        for (var d = destDir; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d))
                            missing.Insert(0, d);
                        Directory.CreateDirectory(destDir);
                        foreach (var dir in missing)
                            log.Add(new FileOperation
                            {
                                Type = FileOperationType.CreateDir,
                                OriginalPath = string.Empty,
                                NewPath = dir,
                                BatchId = batchId,
                                IsRemote = false
                            });
                    }

                    var from = staged.TryGetValue(op, out var tmpPath) ? tmpPath : op.OriginalPath;
                    if (File.Exists(from))
                    {
                        try { File.Move(from, op.NewPath); }
                        catch when (from != op.OriginalPath)
                        {
                            // Non è andata: il file torna al suo nome di prima
                            try { File.Move(from, op.OriginalPath); } catch { }
                            throw;
                        }
                        log.Add(new FileOperation
                        {
                            Type = op.Type,
                            OriginalPath = op.OriginalPath,
                            NewPath = op.NewPath,
                            BatchId = batchId,
                            IsRemote = false
                        });
                        applied++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Errore durante operazione su {File}", op.OriginalPath);
                }
            }
        }
        finally
        {
            if (log.Count > 0)
            {
                try { await _repository.InsertManyAsync(log); }
                catch (Exception ex) { _logger.LogError(ex, "Errore salvataggio log undo"); }
            }
        }

        return applied;
    }

    // ============================================================
    //  APPLY — REMOTO
    // ============================================================

    public async Task<int> ApplyRemoteAsync(
        List<ProposedOperation> operations, CancellationToken ct = default)
    {
        var applied = 0;
        var batchId = Guid.NewGuid().ToString("N");
        var log = new List<FileOperation>();
        var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Catena di rinomine: come in locale, prima tutti su un nome temporaneo
        var todo = operations.Where(o => !o.IsDuplicate && !o.HasConflict).ToList();
        var sources = todo.Select(o => o.OriginalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = new Dictionary<ProposedOperation, string>();
        if (todo.Any(o => sources.Contains(o.NewPath)))
        {
            int i = 0;
            foreach (var op in todo)
            {
                var tmp = TempName(op.OriginalPath, '/', batchId, i++);
                try { await Remote.RenameAsync(op.OriginalPath, tmp, ct); staged[op] = tmp; }
                catch (Exception ex) { _logger.LogError(ex, "Rinomina temporanea remota fallita: {File}", op.OriginalPath); }
            }
        }

        try
        {
            foreach (var op in operations)
            {
                ct.ThrowIfCancellationRequested();
                if (op.IsDuplicate || op.HasConflict) continue;

                try
                {
                    var destDir = RemoteDirName(op.NewPath);
                    if (!string.IsNullOrEmpty(destDir) && createdDirs.Add(destDir))
                    {
                        // Dal livello più esterno al più interno (pattern con più "/")
                        var chain = new List<string>();
                        for (var d = destDir; d.Length > 1; d = RemoteDirName(d)) chain.Insert(0, d);
                        foreach (var dir in chain)
                        {
                            if (dir != destDir && !createdDirs.Add(dir)) continue;
                            try { await Remote.CreateDirectoryAsync(dir, ct); }
                            catch { /* già esiste */ }
                        }
                    }

                    var from = staged.TryGetValue(op, out var tmpPath) ? tmpPath : op.OriginalPath;
                    try { await Remote.RenameAsync(from, op.NewPath, ct); }
                    catch when (from != op.OriginalPath)
                    {
                        try { await Remote.RenameAsync(from, op.OriginalPath, ct); } catch { }
                        throw;
                    }

                    log.Add(new FileOperation
                    {
                        Type = op.Type,
                        OriginalPath = op.OriginalPath,
                        NewPath = op.NewPath,
                        BatchId = batchId,
                        IsRemote = true,
                        ServerKey = Remote.ServerKey
                    });

                    applied++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Errore operazione remota su {File}", op.OriginalPath);
                }
            }
        }
        finally
        {
            if (log.Count > 0)
            {
                try { await _repository.InsertManyAsync(log); }
                catch (Exception ex) { _logger.LogError(ex, "Errore salvataggio log undo remoto"); }
            }
        }

        return applied;
    }

    // ============================================================
    //  UNDO
    // ============================================================

    public async Task<int> UndoLastBatchAsync(CancellationToken ct = default)
    {
        var lastBatch = await _repository.GetLastBatchAsync(isRemote: false);
        if (lastBatch.Count == 0) return 0;

        int undone = 0;

        // Prima tutti i file su un nome temporaneo: rimettendo a posto una catena (Ep_02 → Ep_03,
        // Ep_03 → Ep_04...) il nome originale di un file è ancora occupato da un altro. Prima
        // questi file venivano saltati in silenzio e segnati comunque come annullati.
        var files = lastBatch.Where(o => o.Type is FileOperationType.Move or FileOperationType.Rename).ToList();
        var staged = new Dictionary<FileOperation, string>();
        var batchTag = Guid.NewGuid().ToString("N");
        int k = 0;
        foreach (var op in files)
        {
            if (!File.Exists(op.NewPath)) continue;
            var tmp = TempName(op.NewPath, Path.DirectorySeparatorChar, batchTag, k++);
            try { File.Move(op.NewPath, tmp); staged[op] = tmp; }
            catch (Exception ex) { _logger.LogError(ex, "Undo: rinomina temporanea fallita {File}", op.NewPath); }
        }
        foreach (var op in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!staged.TryGetValue(op, out var tmp)) continue;   // file non più al suo posto: resta da annullare
            try
            {
                var origDir = Path.GetDirectoryName(op.OriginalPath);
                if (!string.IsNullOrEmpty(origDir) && !Directory.Exists(origDir)) Directory.CreateDirectory(origDir);
                if (File.Exists(op.OriginalPath))
                {
                    File.Move(tmp, op.NewPath);   // il nome di prima è occupato da un file estraneo: non si tocca niente
                    _logger.LogWarning("Undo saltato: {File} esiste già", op.OriginalPath);
                    continue;
                }
                File.Move(tmp, op.OriginalPath);
                await _repository.MarkUndoneAsync(op.Id);
                undone++;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Move(tmp, op.NewPath); } catch { }
                _logger.LogError(ex, "Errore undo per operazione {Id}", op.Id);
            }
        }

        // Poi le cartelle create dal gruppo, dalla più interna, se sono rimaste vuote
        foreach (var op in lastBatch.Where(o => o.Type == FileOperationType.CreateDir).OrderByDescending(o => o.NewPath.Length))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(op.NewPath) && Directory.GetFileSystemEntries(op.NewPath).Length == 0)
                    Directory.Delete(op.NewPath);
                await _repository.MarkUndoneAsync(op.Id);
                undone++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore undo per operazione {Id}", op.Id);
            }
        }

        return undone;
    }

    public async Task<int> UndoLastRemoteBatchAsync(CancellationToken ct = default)
    {
        var lastBatch = await _repository.GetLastBatchAsync(isRemote: true);
        if (lastBatch.Count == 0) return 0;

        // L'ultima operazione è stata fatta su un altro server: annullarla qui rinominerebbe
        // file sbagliati (o inesistenti). Si chiede di scegliere quel server.
        var batchServer = lastBatch[0].ServerKey;
        if (!string.IsNullOrEmpty(batchServer) &&
            !string.Equals(batchServer, Remote.ServerKey, StringComparison.OrdinalIgnoreCase))
        {
            var host = batchServer.Split(':')[0];
            throw new InvalidOperationException(
                $"l'ultima operazione è stata fatta su un altro server ({host}): sceglilo nel menu Server per annullarla");
        }

        int undone = 0;

        // Due passaggi, come in locale: una catena si rimette a posto senza nomi occupati
        var files = lastBatch.Where(o => o.Type is FileOperationType.Move or FileOperationType.Rename).ToList();
        var staged = new Dictionary<FileOperation, string>();
        var batchTag = Guid.NewGuid().ToString("N");
        int k = 0;
        foreach (var op in files)
        {
            var tmp = TempName(op.NewPath, '/', batchTag, k++);
            try { await Remote.RenameAsync(op.NewPath, tmp, ct); staged[op] = tmp; }
            catch (Exception ex) { _logger.LogError(ex, "Undo remoto: rinomina temporanea fallita {File}", op.NewPath); }
        }

        foreach (var op in lastBatch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (op.Type is FileOperationType.Move or FileOperationType.Rename)
                {
                    if (!staged.TryGetValue(op, out var tmp)) continue;
                    try { await Remote.RenameAsync(tmp, op.OriginalPath, ct); }
                    catch
                    {
                        try { await Remote.RenameAsync(tmp, op.NewPath, ct); } catch { }
                        throw;
                    }
                }

                await _repository.MarkUndoneAsync(op.Id);
                undone++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore undo remoto per operazione {Id}", op.Id);
            }
        }

        return undone;
    }

    // ============================================================
    //  HISTORY
    // ============================================================

    public Task<List<FileOperation>> GetHistoryAsync(int limit = 200, CancellationToken ct = default)
        => _repository.GetRecentOperationsAsync(limit);

    public Task ClearHistoryAsync(CancellationToken ct = default)
        => _repository.ClearHistoryAsync();

    // ============================================================
    //  HELPERS
    // ============================================================

    private static string RemoteFileName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path.Substring(idx + 1) : path;
    }

    /// <summary>
    /// Pulisce un nome che può contenere sottocartelle ("{Series}/{Episode}" → "Frieren/005"):
    /// ogni pezzo viene ripulito, la "/" resta come separatore. Niente ".." per non uscire dalla destinazione.
    /// </summary>
    private static string SanitizeRelative(string name, char separator)
    {
        var parts = name.Split('/', '\\')
            .Select(p => Sanitize(p))
            .Where(p => p.Length > 0 && p != "." && p != "..")
            .ToList();
        return parts.Count == 0 ? Sanitize(name) : string.Join(separator, parts);
    }

    private static string RemoteDirName(string path)
    {
        var idx = path.LastIndexOf('/');
        if (idx < 0) return "";
        if (idx == 0) return "/";
        return path.Substring(0, idx);
    }

    /// <summary>Nome della serie dal nome del file; se non si riconosce, il nome della cartella.</summary>
    private static string SeriesOrFolder(string fileName, string folderName)
    {
        var series = ExtractSeriesName(fileName);
        return string.IsNullOrWhiteSpace(series) ? folderName : series;
    }

    /// <summary>
    /// Nome della sottocartella di "Dividi cartella". Segnaposto: {Number} (numero della
    /// cartella), {Start}/{End} (primo e ultimo episodio), {Series} (nome della serie).
    /// </summary>
    public static string BuildSplitFolderName(string pattern, int folderNumber, int rangeStart, int rangeEnd, string series = "")
    {
        var result = pattern.Replace("{Series}", series, StringComparison.OrdinalIgnoreCase);
        result = SplitFolderNumberRegex.Replace(result, m =>
        {
            var format = m.Groups[1].Success ? m.Groups[1].Value : "D0";
            return format == "D0" ? folderNumber.ToString() : folderNumber.ToString(format);
        });
        result = SplitFolderStartRegex.Replace(result, m =>
        {
            var format = m.Groups[1].Success ? m.Groups[1].Value : "D0";
            return format == "D0" ? rangeStart.ToString() : rangeStart.ToString(format);
        });
        result = SplitFolderEndRegex.Replace(result, m =>
        {
            var format = m.Groups[1].Success ? m.Groups[1].Value : "D0";
            return format == "D0" ? rangeEnd.ToString() : rangeEnd.ToString(format);
        });
        return result;
    }

    private static string ExtractSeriesName(string fileName)
    {
        var marker = EpisodeMarkerRegex.Match(fileName);
        if (marker.Success && marker.Index > 0)
            return CleanSeriesName(fileName.Substring(0, marker.Index));

        var isolated = IsolatedNumberRegex.Matches(fileName);
        if (isolated.Count > 0)
        {
            var last = isolated[isolated.Count - 1];
            if (last.Index > 0)
                return CleanSeriesName(fileName.Substring(0, last.Index));
        }

        return CleanSeriesName(fileName);
    }

    private static EpisodeInfo ExtractEpisodeInfo(string fileName)
    {
        var m1 = EpisodeMarkerRegex.Match(fileName);
        if (m1.Success)
        {
            var info = ParseEpisodeString(m1.Groups[1].Value);
            if (info.Start > 0) return info;
        }

        var isolatedMatches = IsolatedNumberRegex.Matches(fileName);
        if (isolatedMatches.Count > 0)
        {
            var last = isolatedMatches[isolatedMatches.Count - 1];
            var info = ParseEpisodeString(last.Groups[1].Value);
            if (info.Start > 0) return info;
        }

        var firstNumber = FirstNumberRegex.Match(fileName);
        if (firstNumber.Success && int.TryParse(firstNumber.Groups[1].Value, out var n3) && n3 > 0)
            return new EpisodeInfo(n3, n3, 1, n3.ToString(), false);

        return new EpisodeInfo(0, 0, 0, "", false);
    }

    private static EpisodeInfo ParseEpisodeString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return new EpisodeInfo(0, 0, 0, "", false);
        var parts = raw.Split(new[] { '-', '.', '_' }, StringSplitOptions.RemoveEmptyEntries);

        var numbers = new List<int>();
        foreach (var p in parts)
            if (int.TryParse(p, out var n) && n > 0) numbers.Add(n);

        if (numbers.Count == 0) return new EpisodeInfo(0, 0, 0, "", false);
        if (numbers.Count == 1) return new EpisodeInfo(numbers[0], numbers[0], 1, numbers[0].ToString(), false);

        int min = numbers.Min();
        int max = numbers.Max();
        int count = max - min + 1;
        return new EpisodeInfo(min, max, count, string.Join("-", Enumerable.Range(min, count)), true);
    }

    private static string ExtractTitle(string fileName)
    {
        var match = TitleRegex.Match(fileName);
        if (match.Success && match.Groups[1].Success)
        {
            var title = match.Groups[1].Value.Trim(' ', '-', '_', '.');
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }
        return string.Empty;
    }

    private static string ApplyPatternWithRange(
        string pattern, string series, EpisodeInfo info, string title, string originalFileName)
    {
        if (info.HasRange)
        {
            pattern = EpisodeRangeRegex.Replace(pattern, m =>
            {
                var format = m.Groups[1].Success ? m.Groups[1].Value : null;
                var numbers = Enumerable.Range(info.Start, info.Count);
                var formatted = numbers.Select(n =>
                    !string.IsNullOrEmpty(format) ? n.ToString(format) : n.ToString());
                return string.Join("-", formatted);
            });
        }

        return FileNameBuilder.ApplyPattern(pattern, series, info.Start, title, originalFileName);
    }

    private static string CleanSeriesName(string name)
    {
        name = CleanSeriesSuffixRegex.Replace(name, "");
        name = CollapseWhitespaceRegex.Replace(name, " ");
        name = CamelCaseSplitRegex.Replace(name, " ");
        return name.Trim();
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid) name = name.Replace(c, '_');
        return name.Trim();
    }
}