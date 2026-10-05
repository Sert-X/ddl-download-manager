using DownloadManager.Models;
using Microsoft.Extensions.Logging;
using WinSCP;

namespace DownloadManager.Services.Sftp;

public class SftpDownloadQueueService : SftpQueueServiceBase<SftpDownloadJob>
{
    public SftpDownloadQueueService(ILogger<SftpDownloadQueueService> logger, SftpSessionLimiter sessionLimiter)
    : base(logger, sessionLimiter) { }

    protected override SftpJobStatus ActiveStatus => SftpJobStatus.Downloading;
    protected override string LogCategory => "Download";
    protected override string ConcurrencyLogLabel => "SFTP download";

    protected override void SetProgressBytes(SftpDownloadJob job, long bytes) => job.DownloadedBytes = bytes;
    protected override void SetCompletedBytes(SftpDownloadJob job) => job.DownloadedBytes = job.TotalBytes;
    protected override string GetJobLogDescription(SftpDownloadJob job) => job.RemotePath;

    // ============================================================
    //  TRASFERIMENTO
    // ============================================================

    protected override async Task ExecuteTransferAsync(Session session, SftpDownloadJob job, CancellationToken ct)
    {
        var localDir = Path.GetDirectoryName(job.LocalPath);
        if (string.IsNullOrEmpty(localDir))
            localDir = Directory.GetCurrentDirectory();

        Directory.CreateDirectory(localDir);

        var transferOptions = new TransferOptions
        {
            ResumeSupport = new TransferResumeSupport { State = TransferResumeSupportState.On },
            TransferMode = TransferMode.Binary
        };

        // WinSCP: GetFileToDirectory scarica un singolo file SENZA interpretare
        // wildcard come [ ] * ?. Con GetFiles, nomi tipo 'film [m1080p].mp4'
        // vengono interpretati come mask e falliscono.
        var remoteFileName = Path.GetFileName(job.RemotePath.Replace('\\', '/'));
        var expectedFile = Path.Combine(localDir, remoteFileName);

        _logger.LogInformation(
            "Job #{Id}: GetFileToDirectory remote='{Remote}' → dir='{Dir}' (atteso: '{Expected}')",
            job.Id, job.RemotePath, localDir, expectedFile);

        try
        {
            await Task.Run(() =>
                session.GetFileToDirectory(job.RemotePath, localDir, false, transferOptions), ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        // GetFileToDirectory lancia eccezione su errore: se siamo qui, il trasferimento è
        // riuscito. Verifica difensiva che il file esista davvero.
        if (!File.Exists(expectedFile))
        {
            var filesInDir = Directory.Exists(localDir)
                ? string.Join(", ", Directory.GetFiles(localDir).Select(Path.GetFileName).Take(10))
                : "(cartella inesistente)";

            throw new InvalidOperationException(
                $"Download riportato come riuscito, ma '{expectedFile}' non esiste. " +
                $"File presenti: {filesInDir}");
        }

        if (!string.Equals(expectedFile, job.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(job.LocalPath))
            {
                try { File.Delete(job.LocalPath); } catch { }
            }
            File.Move(expectedFile, job.LocalPath);
            _logger.LogInformation("Job #{Id}: rinominato '{From}' → '{To}'",
                job.Id, expectedFile, job.LocalPath);
        }
    }
}