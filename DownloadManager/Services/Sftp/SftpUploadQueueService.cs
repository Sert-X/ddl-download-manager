using DownloadManager.Models;
using Microsoft.Extensions.Logging;
using WinSCP;

namespace DownloadManager.Services.Sftp;

public class SftpUploadQueueService : SftpQueueServiceBase<SftpUploadJob>
{
    private readonly HashSet<string> _ensuredDirs = new(StringComparer.OrdinalIgnoreCase);

    public SftpUploadQueueService(ILogger<SftpUploadQueueService> logger, SftpSessionLimiter sessionLimiter)
    : base(logger, sessionLimiter) { }

    protected override SftpJobStatus ActiveStatus => SftpJobStatus.Uploading;
    protected override string LogCategory => "Upload";
    protected override string ConcurrencyLogLabel => "SFTP";

    protected override void SetProgressBytes(SftpUploadJob job, long bytes) => job.UploadedBytes = bytes;
    protected override void SetCompletedBytes(SftpUploadJob job) => job.UploadedBytes = job.TotalBytes;
    protected override string GetJobLogDescription(SftpUploadJob job) => job.LocalPath;

    public override void Configure(SftpConfig config)
    {
        base.Configure(config);
        _ensuredDirs.Clear();
    }

    // ============================================================
    //  ENQUEUE SPECIFICO UPLOAD
    // ============================================================

    public async Task<int> EnqueueFolderAsync(string localFolder, string remoteBaseFolder)
    {
        if (CurrentConfig == null)
            throw new InvalidOperationException("Nessuna configurazione SFTP impostata.");

        if (!Directory.Exists(localFolder))
            throw new DirectoryNotFoundException($"Cartella non trovata: {localFolder}");

        var allFiles = Directory.GetFiles(localFolder, "*", SearchOption.AllDirectories);
        var localFolderName = Path.GetFileName(localFolder.TrimEnd(Path.DirectorySeparatorChar));
        var remoteTarget = CombineRemote(remoteBaseFolder, localFolderName);

        int count = 0;

        foreach (var localFile in allFiles)
        {
            var relativePath = Path.GetRelativePath(localFolder, localFile).Replace('\\', '/');
            var remoteFile = CombineRemote(remoteTarget, relativePath);

            var job = new SftpUploadJob
            {
                LocalPath = localFile,
                RemotePath = remoteFile,
                TotalBytes = new FileInfo(localFile).Length,
                Status = SftpJobStatus.Pending
            };

            await EnqueueJobAsync(job);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Accoda file e/o cartelle locali verso una cartella remota.
    /// Le cartelle vengono espanse in una sottocartella con lo stesso nome;
    /// i file vengono accodati direttamente.
    /// </summary>
    public async Task<int> EnqueuePathsAsync(IEnumerable<string> localPaths, string remoteBaseFolder)
    {
        if (CurrentConfig == null)
            throw new InvalidOperationException("Nessuna configurazione SFTP impostata.");

        int total = 0;

        foreach (var localPath in localPaths)
        {
            if (Directory.Exists(localPath))
            {
                total += await EnqueueFolderAsync(localPath, remoteBaseFolder);
            }
            else if (File.Exists(localPath))
            {
                var fileName = Path.GetFileName(localPath);
                var remotePath = CombineRemote(remoteBaseFolder, fileName);

                var job = new SftpUploadJob
                {
                    LocalPath = localPath,
                    RemotePath = remotePath,
                    TotalBytes = new FileInfo(localPath).Length,
                    Status = SftpJobStatus.Pending
                };

                await EnqueueJobAsync(job);
                total++;
            }
        }

        return total;
    }

    // ============================================================
    //  TRASFERIMENTO
    // ============================================================

    protected override async Task ExecuteTransferAsync(Session session, SftpUploadJob job, CancellationToken ct)
    {
        var remoteDir = Path.GetDirectoryName(job.RemotePath)?.Replace('\\', '/') ?? "";
        if (!string.IsNullOrEmpty(remoteDir))
            EnsureRemoteDir(session, remoteDir);

        var transferOptions = new TransferOptions
        {
            ResumeSupport = new TransferResumeSupport { State = TransferResumeSupportState.On },
            TransferMode = TransferMode.Binary
        };

        TransferOperationResult result;

        try
        {
            result = await Task.Run(() => session.PutFiles(job.LocalPath, job.RemotePath, false, transferOptions), ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        if (!result.IsSuccess)
        {
            var error = string.Join("; ", result.Failures.Select(f => f.Message));
            throw new InvalidOperationException(error);
        }
    }

    // ============================================================
    //  HELPERS
    // ============================================================

    private void EnsureRemoteDir(Session session, string remoteDir)
    {
        if (string.IsNullOrEmpty(remoteDir)) return;
        if (!_ensuredDirs.Add(remoteDir)) return;

        var parts = remoteDir.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var p in parts)
        {
            current += "/" + p;
            try { session.CreateDirectory(current); }
            catch { }
        }
    }

    private static string CombineRemote(string basePath, string relative)
    {
        if (string.IsNullOrEmpty(basePath)) basePath = "/";
        var baseNorm = basePath.TrimEnd('/');
        var rel = relative.Replace('\\', '/').TrimStart('/');
        if (baseNorm == "") return "/" + rel;
        return baseNorm + "/" + rel;
    }
}