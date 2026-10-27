using Avalonia.Threading;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;
using WinSCP;

namespace DownloadManager.Services.Sftp;

/// <summary>
/// Coda delle copie tra due server SFTP. Ogni job apre una sessione sul server di
/// origine (quella della coda base) e una sul server di destinazione; il file passa
/// dal PC in streaming: si legge da una parte e si scrive dall'altra nello stesso
/// momento, senza file temporanei e senza aspettare la fine del download.
/// </summary>
public class SftpRelayQueueService : SftpQueueServiceBase<SftpRelayJob>
{
    private readonly HashSet<string> _ensuredDirs = new(StringComparer.Ordinal);
    private readonly object _dirsLock = new();

    public SftpRelayQueueService(ILogger<SftpRelayQueueService> logger)
        : base(logger, new SftpSessionLimiter(32)) { }   // limite proprio: non toglie slot alla tab SFTP

    protected override SftpJobStatus ActiveStatus => SftpJobStatus.Transferring;
    protected override string LogCategory => "Copia tra server";
    protected override string ConcurrencyLogLabel => "SFTP server→server";

    // Ogni job ha i suoi due server: la configurazione della coda non serve.
    protected override bool IsConfigured => true;
    protected override SftpConfig? GetSessionConfig(SftpRelayJob job) => job.SourceConfig;
    // Limite proprio della coda (32 sessioni): non dipende dal server principale
    protected override SftpSessionLimiter LimiterFor(SftpRelayJob job) => _sessionLimiter;
    protected override bool UsesSessionProgress => false;

    protected override void SetProgressBytes(SftpRelayJob job, long bytes) => job.TransferredBytes = bytes;
    protected override void SetCompletedBytes(SftpRelayJob job) => job.TransferredBytes = job.TotalBytes;
    protected override string GetJobLogDescription(SftpRelayJob job) =>
        $"{job.SourceServer}:{job.SourcePath} → {job.DestinationServer}:{job.DestinationPath}";

    protected override async Task ExecuteTransferAsync(Session source, SftpRelayJob job, CancellationToken ct)
    {
        using var destination = new Session();
        // Annullando/mettendo in pausa si interrompe anche la scrittura sul secondo server
        using var abortReg = ct.Register(() => { try { destination.Abort(); } catch { } });

        if (!await TryOpenWithRetryAsync(destination, CreateSessionOptions(job.DestinationConfig), ct))
            throw new InvalidOperationException($"Impossibile connettersi a '{job.DestinationServer}'.");

        EnsureRemoteDir(destination, job.DestinationConfig, TransferJobBase.GetRemoteParent(job.DestinationPath));

        var options = new TransferOptions { TransferMode = TransferMode.Binary };

        try
        {
            // Thread dedicato: la copia lo tiene occupato fino alla fine del file
            await BlockingWork.Run(() =>
            {
                using var input = source.GetFile(job.SourcePath, options);
                using var counted = new ProgressStream(input, (bytes, speed) =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        SetProgressBytes(job, bytes);
                        job.SpeedBytesPerSecond = speed;
                    }));
                destination.PutFile(counted, job.DestinationPath, options);
            }, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }
    }

    /// <summary>Crea le cartelle mancanti sul server di destinazione (una volta per cartella).</summary>
    private void EnsureRemoteDir(Session session, SftpConfig server, string remoteDir)
    {
        if (string.IsNullOrEmpty(remoteDir) || remoteDir == "/") return;
        lock (_dirsLock)
        {
            if (!_ensuredDirs.Add($"{server.Host}:{server.Port}|{remoteDir}")) return;
        }

        var current = "";
        foreach (var part in remoteDir.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            if (session.FileExists(current)) continue;
            try { session.CreateDirectory(current); }
            catch (Exception ex) { _logger.LogWarning(ex, "Creazione cartella {Dir} su {Server} fallita", current, server.Name); }
        }
    }

    /// <summary>Le cartelle potrebbero essere state eliminate: si ricontrollano al prossimo job.</summary>
    public void ForgetCreatedFolders()
    {
        lock (_dirsLock) _ensuredDirs.Clear();
    }

    /// <summary>Stream in sola lettura che conta i byte letti e calcola la velocità.</summary>
    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long, double> _report;
        private long _read;
        private long _lastReportTick;
        private long _windowStartTick = Environment.TickCount64;
        private long _windowStartBytes;
        private double _speed;

        public ProgressStream(Stream inner, Action<long, double> report)
        {
            _inner = inner;
            _report = report;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            Count(n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = _inner.Read(buffer);
            Count(n);
            return n;
        }

        private void Count(int n)
        {
            if (n <= 0) { _report(_read, 0); return; }
            _read += n;

            var now = Environment.TickCount64;
            var window = now - _windowStartTick;
            if (window >= 1000)
            {
                _speed = (_read - _windowStartBytes) * 1000.0 / window;
                _windowStartTick = now;
                _windowStartBytes = _read;
            }
            if (now - _lastReportTick >= 200)
            {
                _lastReportTick = now;
                _report(_read, _speed);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
