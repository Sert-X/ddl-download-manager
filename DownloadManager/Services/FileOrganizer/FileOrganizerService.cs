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

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".m4v"
    };

    private static readonly Regex EpisodeMarkerRegex = new(
        @"(?:^|[\s_\-\(\[\._])(?:Ep|Episode|E|Puntata|P)[\s_\-\.]*(\d{1,4}(?:[\-\._]\d{1,4})*)(?:[\s_\-\.\)\]]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IsolatedNumberRegex = new(
        @"(?:^|[\s_\-\.\(\[_])(\d{1,4}(?:[\-\._]\d{1,4})*)(?:[\s_\-\.\)\]_]|$)",
        RegexOptions.Compiled);

    private static readonly Regex FirstNumberRegex = new(@"(\d{1,4})", RegexOptions.Compiled);

    private static readonly Regex EpisodeStartRegex = new(
        @"[\s_\-\._](?:Ep|Episode|Puntata|E\d|\d{1,4}[\s_\-\._])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        string folder, string pattern, bool createSubfolders, CancellationToken ct = default)
        => Task.Run(() => PreviewInternal(folder, pattern, createSubfolders, ct), ct);

    private List<ProposedOperation> PreviewInternal(
        string folder, string pattern, bool createSubfolders, CancellationToken ct)
    {
        var result = new List<ProposedOperation>();

        if (!Directory.Exists(folder))
        {
            _logger.LogWarning("Cartella non trovata: {Folder}", folder);
            return result;
        }

        var files = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        _logger.LogInformation("Trovati {Count} file video in {Folder}", files.Count, folder);

        var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var ext = Path.GetExtension(file).TrimStart('.');

                var series = ExtractSeriesName(fileName);
                if (string.IsNullOrWhiteSpace(series)) series = folderName;

                var info = ExtractEpisodeInfo(fileName);
                var title = ExtractTitle(fileName);
                var newName = ApplyPatternWithRange(pattern, series, info, title, fileName);

                var destFolder = folder;
                if (createSubfolders && !string.IsNullOrWhiteSpace(series))
                    destFolder = Path.Combine(folder, Sanitize(series));

                var newPath = Path.Combine(destFolder, Sanitize(newName) + "." + ext);

                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                var op = new ProposedOperation
                {
                    OriginalPath = file,
                    NewPath = newPath,
                    SourceFolder = folder,
                    Type = string.Equals(Path.GetDirectoryName(file), destFolder, StringComparison.OrdinalIgnoreCase)
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
                _logger.LogError(ex, "Errore analisi file {File}", file);
            }
        }

        return result;
    }

    // ============================================================
    //  PREVIEW — LOCALE, MERGE
    // ============================================================

    public Task<List<ProposedOperation>> PreviewMergeAsync(
        List<MergeSourceFolder> sourceFolders, string destinationFolder,
        string pattern, bool createSubfolders, CancellationToken ct = default)
        => Task.Run(() => PreviewMergeInternal(sourceFolders, destinationFolder, pattern, createSubfolders, ct), ct);

    private List<ProposedOperation> PreviewMergeInternal(
        List<MergeSourceFolder> sourceFolders, string destinationFolder,
        string pattern, bool createSubfolders, CancellationToken ct)
    {
        var result = new List<ProposedOperation>();
        if (sourceFolders.Count == 0) return result;

        var ordered = sourceFolders.OrderBy(f => f.Priority).ToList();
        int globalEpisodeCounter = 0;

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

                    var newPath = Path.Combine(destFolder, Sanitize(newName) + "." + ext);

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

            var folderNameBuilt = BuildSplitFolderName(folderPattern, segNum, rangeStart, rangeEnd);
            var subFolder = Path.Combine(destinationFolder, Sanitize(folderNameBuilt));

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
                var newPath = Path.Combine(subFolder, Sanitize(newName) + "." + ext);

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

        return result;
    }

    // ============================================================
    //  PREVIEW — REMOTO, SINGOLA CARTELLA
    // ============================================================

    public async Task<List<ProposedOperation>> PreviewRemoteFolderAsync(
        string remoteFolder, string pattern, bool createSubfolders, CancellationToken ct = default)
    {
        var result = new List<ProposedOperation>();

        if (string.IsNullOrWhiteSpace(remoteFolder)) return result;
        if (!_sftpService.IsConnected)
            throw new InvalidOperationException("Non connesso al server SFTP.");

        var remoteBase = remoteFolder.TrimEnd('/');
        if (string.IsNullOrEmpty(remoteBase)) remoteBase = "/";

        var entries = await _sftpService.ListDirectoryAsync(remoteBase, ct);
        var files = entries
            .Where(e => !e.IsDirectory && VideoExtensions.Contains(Path.GetExtension(e.Name)))
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

        foreach (var entry in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var fileName = Path.GetFileNameWithoutExtension(entry.Name);
                var ext = Path.GetExtension(entry.Name).TrimStart('.');

                var series = ExtractSeriesName(fileName);
                if (string.IsNullOrWhiteSpace(series)) series = folderName;

                var info = ExtractEpisodeInfo(fileName);
                var title = ExtractTitle(fileName);
                var newName = Sanitize(ApplyPatternWithRange(pattern, series, info, title, fileName)) + "." + ext;

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

                await CheckRemoteConflictAsync(op, destFolder, newName, listedDirs, assignedTargets, ct, entry.FullPath);
                result.Add(op);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore analisi file remoto {File}", entry.FullPath);
            }
        }

        return result;
    }

    // ============================================================
    //  PREVIEW — REMOTO, MERGE
    // ============================================================

    public async Task<List<ProposedOperation>> PreviewRemoteMergeAsync(
        List<MergeSourceFolder> sourceFolders, string remoteDestinationFolder,
        string pattern, bool createSubfolders, CancellationToken ct = default)
    {
        var result = new List<ProposedOperation>();
        if (sourceFolders.Count == 0) return result;
        if (!_sftpService.IsConnected)
            throw new InvalidOperationException("Non connesso al server SFTP.");

        var remoteDest = remoteDestinationFolder.TrimEnd('/');
        if (string.IsNullOrEmpty(remoteDest)) remoteDest = "/";

        var ordered = sourceFolders.OrderBy(f => f.Priority).ToList();
        int globalEpisodeCounter = 0;

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
                entries = await _sftpService.ListDirectoryAsync(sourcePath, ct);
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
                    var newName = Sanitize(ApplyPatternWithRange(pattern, series, newInfo, title, fileName)) + "." + ext;

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

                    await CheckRemoteConflictAsync(op, destFolder, newName, listedDirs, assignedTargets, ct, entry.FullPath);
                    result.Add(op);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Errore analisi file remoto {File}", item.Entry.FullPath);
                }
            }
        }

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
        if (!_sftpService.IsConnected)
            throw new InvalidOperationException("Non connesso al server SFTP.");
        if (segmentSizes == null || segmentSizes.Count == 0) return result;
        if (string.IsNullOrWhiteSpace(folderPattern)) folderPattern = "{Number}";

        var sourceBase = remoteSourceFolder.TrimEnd('/');
        if (string.IsNullOrEmpty(sourceBase)) sourceBase = "/";

        var destBase = remoteDestinationFolder.TrimEnd('/');
        if (string.IsNullOrEmpty(destBase)) destBase = sourceBase;

        var entries = await _sftpService.ListDirectoryAsync(sourceBase, ct);
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

            var folderNameBuilt = BuildSplitFolderName(folderPattern, segNum, rangeStart, rangeEnd);
            var subFolder = destBase.TrimEnd('/') + "/" + Sanitize(folderNameBuilt);

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
                var newName = Sanitize(ApplyPatternWithRange(filePattern, series, newInfo, title, fileName)) + "." + ext;
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

                await CheckRemoteConflictAsync(op, subFolder, newName, listedDirs, assignedTargets, ct, entry.FullPath);
                result.Add(op);
            }

            if (mode == SplitNumberingMode.Reset) globalCounter += accumulated;
        }

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
                var destEntries = await _sftpService.ListDirectoryAsync(destFolder, ct);
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
    //  APPLY — LOCALE
    // ============================================================

    public async Task<int> ApplyAsync(List<ProposedOperation> operations, CancellationToken ct = default)
    {
        var applied = 0;
        var batchId = Guid.NewGuid().ToString("N");
        var log = new List<FileOperation>();

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
                        Directory.CreateDirectory(destDir);
                        log.Add(new FileOperation
                        {
                            Type = FileOperationType.CreateDir,
                            OriginalPath = string.Empty,
                            NewPath = destDir,
                            BatchId = batchId,
                            IsRemote = false
                        });
                    }

                    if (File.Exists(op.OriginalPath))
                    {
                        File.Move(op.OriginalPath, op.NewPath);
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
                        try { await _sftpService.CreateDirectoryAsync(destDir, ct); }
                        catch { /* già esiste */ }
                    }

                    await _sftpService.RenameAsync(op.OriginalPath, op.NewPath, ct);

                    log.Add(new FileOperation
                    {
                        Type = op.Type,
                        OriginalPath = op.OriginalPath,
                        NewPath = op.NewPath,
                        BatchId = batchId,
                        IsRemote = true
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

        foreach (var op in lastBatch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (op.Type)
                {
                    case FileOperationType.Move:
                    case FileOperationType.Rename:
                        if (File.Exists(op.NewPath))
                        {
                            var origDir = Path.GetDirectoryName(op.OriginalPath);
                            if (!string.IsNullOrEmpty(origDir) && !Directory.Exists(origDir))
                                Directory.CreateDirectory(origDir);
                            if (!File.Exists(op.OriginalPath))
                                File.Move(op.NewPath, op.OriginalPath);
                        }
                        break;

                    case FileOperationType.CreateDir:
                        if (Directory.Exists(op.NewPath) &&
                            Directory.GetFileSystemEntries(op.NewPath).Length == 0)
                            Directory.Delete(op.NewPath);
                        break;
                }

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

        int undone = 0;

        foreach (var op in lastBatch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (op.Type)
                {
                    case FileOperationType.Move:
                    case FileOperationType.Rename:
                        await _sftpService.RenameAsync(op.NewPath, op.OriginalPath, ct);
                        break;

                    case FileOperationType.CreateDir:
                        // Non annulliamo la creazione cartella remota.
                        break;
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

    private static string RemoteDirName(string path)
    {
        var idx = path.LastIndexOf('/');
        if (idx < 0) return "";
        if (idx == 0) return "/";
        return path.Substring(0, idx);
    }

    private static string BuildSplitFolderName(string pattern, int folderNumber, int rangeStart, int rangeEnd)
    {
        var result = pattern;
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
        // 1. Preferisci tagliare prima di un marker esplicito (Ep 5, Episode 5, Puntata 5)
        var marker = EpisodeMarkerRegex.Match(fileName);
        if (marker.Success && marker.Index > 0)
            return CleanSeriesName(fileName.Substring(0, marker.Index));

        // 2. Altrimenti taglia prima dell'ULTIMO numero isolato
        // (che è l'episodio), non del primo che potrebbe essere parte del titolo.
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
        // 1. Marker esplicito (Ep, Episode, Puntata, E, P)
        var m1 = EpisodeMarkerRegex.Match(fileName);
        if (m1.Success)
        {
            var info = ParseEpisodeString(m1.Groups[1].Value);
            if (info.Start > 0) return info;
        }

        // 2. Numeri isolati: prendi L'ULTIMO, non il primo.
        // L'episodio è quasi sempre l'ultimo numero prima dell'estensione/tag,
        // mentre il primo numero può far parte del titolo (es. "L'Uomo Tigre 2 (ITA) - 001").
        var isolatedMatches = IsolatedNumberRegex.Matches(fileName);
        if (isolatedMatches.Count > 0)
        {
            var last = isolatedMatches[isolatedMatches.Count - 1];
            var info = ParseEpisodeString(last.Groups[1].Value);
            if (info.Start > 0) return info;
        }

        // 3. Fallback: primo numero qualsiasi
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