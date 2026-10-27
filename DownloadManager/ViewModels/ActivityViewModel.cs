using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Download;
using DownloadManager.Services.Notifications;
using DownloadManager.Services.Sftp;

namespace DownloadManager.ViewModels;

/// <summary>
/// Riepilogo di tutto ciò che sta lavorando (barra di stato in basso, visibile da ogni tab) e
/// notifiche di Windows a fine lavoro: serie completate, download falliti, code SFTP finite,
/// disco pieno. Le notifiche partono solo se l'app non è in primo piano.
/// </summary>
public partial class ActivityViewModel : ObservableObject
{
    private readonly DownloadQueueService _queue;
    private readonly SettingsService _settings;
    private readonly (string Label, string Done, Func<IEnumerable<TransferJobBase>> Source)[] _sftp;
    private readonly DispatcherTimer _timer;

    /// <summary>Mostra una notifica di Windows (lo collega la finestra principale).</summary>
    public Action<string, string, WindowsNotifier.Kind>? Notify { get; set; }

    /// <summary>true se l'utente sta guardando l'app (finestra attiva e non ridotta a icona).</summary>
    public Func<bool>? IsUserLooking { get; set; }

    // ---- barra di stato ----
    [ObservableProperty] private string _animeText = "";
    [ObservableProperty] private bool _hasAnime;
    [ObservableProperty] private bool _animeHasFailed;
    [ObservableProperty] private string _streamingText = "";
    [ObservableProperty] private bool _hasStreaming;
    [ObservableProperty] private bool _streamingHasFailed;
    [ObservableProperty] private string _sftpText = "";
    [ObservableProperty] private bool _hasSftp;
    [ObservableProperty] private bool _sftpHasFailed;
    [ObservableProperty] private bool _isIdle = true;
    [ObservableProperty] private string _animeTip = "";
    [ObservableProperty] private string _streamingTip = "";
    [ObservableProperty] private string _sftpTip = "";

    public bool NotificationsEnabled
    {
        get => _settings.Current.NotificationsEnabled;
        set
        {
            if (_settings.Current.NotificationsEnabled == value) return;
            _settings.Current.NotificationsEnabled = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NotificationsTip));
        }
    }

    public string NotificationsTip => NotificationsEnabled
        ? "Notifiche di Windows attive: avvisano a fine serie, a fine code SFTP, per i download falliti e se il disco è pieno (solo quando l'app non è in primo piano). Clic per spegnerle."
        : "Notifiche di Windows spente. Clic per accenderle.";

    // ---- stato per le notifiche ----
    private sealed class SeriesSession { public int Completed; public int Failed; }
    private readonly Dictionary<(string Cat, string Series), SeriesSession> _series = new();
    private readonly List<string> _failedBatch = new();
    private DateTime _failedBatchSince;
    private readonly HashSet<TransferJobBase>[] _sftpSession;
    private DateTime _lastDiskWarning = DateTime.MinValue;

    public ActivityViewModel(DownloadQueueService queue, SettingsService settings,
        SftpUploadQueueService upload, SftpDownloadQueueService download, SftpRelayQueueService relay)
    {
        _queue = queue;
        _settings = settings;
        _sftp = new (string, string, Func<IEnumerable<TransferJobBase>>)[]
        {
            ("Upload", "caricati", () => upload.AllJobs),
            ("Download SFTP", "scaricati", () => download.AllJobs),
            ("Copie tra server", "copiati", () => relay.AllJobs),
        };
        _sftpSession = _sftp.Select(_ => new HashSet<TransferJobBase>()).ToArray();

        _queue.ItemFinished += OnItemFinished;
        _queue.DiskSpaceLow += OnDiskSpaceLow;

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Update());
        _timer.Start();
        Update();
    }

    // ============================================================
    //  BARRA DI STATO
    // ============================================================

    private static string Speed(double bps) => bps <= 0 ? "" : $"{bps / 1024 / 1024:F1} MB/s";

    private (string Text, bool Has, bool Failed, string Tip) Downloads(string category)
    {
        int active = 0, queued = 0, failed = 0, paused = 0;
        double speed = 0;
        foreach (var i in _queue.AllItems)
        {
            if (i.Category != category) continue;
            switch (i.Status)
            {
                case DownloadStatus.Downloading: active++; speed += i.SpeedBytesPerSecond; break;
                case DownloadStatus.Pending: queued++; break;
                case DownloadStatus.Paused: paused++; break;
                case DownloadStatus.Failed: failed++; break;
            }
        }
        var parts = new List<string>();
        if (active > 0) parts.Add($"{active} in download");
        if (queued > 0) parts.Add($"{queued} in coda");
        if (failed > 0) parts.Add(failed == 1 ? "1 fallito" : $"{failed} falliti");
        if (active > 0 && speed > 0) parts.Add(Speed(speed));
        var tip = parts.Count == 0 ? "" : string.Join(" · ", parts) + (paused > 0 ? $" · {paused} in pausa" : "") + ". Clic per aprire la tab.";
        return (string.Join(" · ", parts), active + queued + failed > 0, failed > 0, tip);
    }

    private void Update()
    {
        var a = Downloads("anime");
        AnimeText = a.Text; HasAnime = a.Has; AnimeHasFailed = a.Failed; AnimeTip = a.Tip;
        var s = Downloads("streaming");
        StreamingText = s.Text; HasStreaming = s.Has; StreamingHasFailed = s.Failed; StreamingTip = s.Tip;

        // SFTP: ↑ upload · ↓ download · ⇄ copie
        int[] act = new int[3], que = new int[3];
        int sftpFailed = 0;
        double sftpSpeed = 0;
        for (int q = 0; q < _sftp.Length; q++)
        {
            foreach (var j in _sftp[q].Source())
            {
                switch (j.Status)
                {
                    case var _ when j.IsActive: act[q]++; sftpSpeed += j.SpeedBytesPerSecond; break;
                    case SftpJobStatus.Pending: que[q]++; break;
                    case SftpJobStatus.Failed: sftpFailed++; break;
                }
            }
        }
        var arrows = new[] { "↑", "↓", "⇄" };
        var sp = new List<string>();
        for (int q = 0; q < 3; q++)
            if (act[q] + que[q] > 0) sp.Add($"{arrows[q]} {act[q]}" + (que[q] > 0 ? $"+{que[q]}" : ""));
        if (sftpFailed > 0) sp.Add(sftpFailed == 1 ? "1 fallito" : $"{sftpFailed} falliti");
        if (sftpSpeed > 0) sp.Add(Speed(sftpSpeed));
        SftpText = string.Join(" · ", sp);
        HasSftp = sp.Count > 0;
        SftpHasFailed = sftpFailed > 0;
        SftpTip = HasSftp
            ? $"Upload {act[0]} attivi, {que[0]} in coda · Download {act[1]} attivi, {que[1]} in coda · Copie {act[2]} attive, {que[2]} in coda" +
              (sftpFailed > 0 ? $" · {sftpFailed} falliti" : "") + ". Clic per aprire la tab SFTP."
            : "";

        IsIdle = !HasAnime && !HasStreaming && !HasSftp;

        CheckSeriesFinished();
        CheckSftpFinished();
        FlushFailures(force: false);
    }

    // ============================================================
    //  NOTIFICHE
    // ============================================================

    private bool CanNotify => NotificationsEnabled && Notify != null && !(IsUserLooking?.Invoke() ?? false);

    private void OnItemFinished(DownloadItem item)
    {
        var key = (item.Category, item.SeriesName);
        if (!_series.TryGetValue(key, out var ses)) _series[key] = ses = new SeriesSession();
        if (item.Status == DownloadStatus.Failed)
        {
            ses.Failed++;
            if (_failedBatch.Count == 0) _failedBatchSince = DateTime.Now;
            var ep = item.EpisodeNumber > 0 && item.SeriesName != "" ? $"{item.SeriesName} ep. {item.EpisodeNumber}" : item.FileName;
            _failedBatch.Add($"{ep}: {item.ErrorMessage}");
        }
        else ses.Completed++;
    }

    /// <summary>I falliti si raccolgono per qualche secondo e si avvisano insieme.</summary>
    private void FlushFailures(bool force)
    {
        if (_failedBatch.Count == 0) return;
        if (!force && DateTime.Now - _failedBatchSince < TimeSpan.FromSeconds(8)) return;
        var n = _failedBatch.Count;
        var text = n == 1 ? _failedBatch[0] : string.Join("\n", _failedBatch.Take(3)) + (n > 3 ? $"\n… e altri {n - 3}" : "");
        _failedBatch.Clear();
        if (CanNotify)
            Notify!(n == 1 ? "Download fallito" : $"{n} download falliti", text, WindowsNotifier.Kind.Error);
    }

    /// <summary>Una serie è finita quando non ha più episodi in coda o in download.</summary>
    private void CheckSeriesFinished()
    {
        if (_series.Count == 0) return;
        var busy = new HashSet<(string, string)>();
        foreach (var i in _queue.AllItems)
            if (i.Status is DownloadStatus.Pending or DownloadStatus.Downloading)
                busy.Add((i.Category, i.SeriesName));

        foreach (var (key, ses) in _series.ToList())
        {
            if (busy.Contains(key)) continue;
            _series.Remove(key);
            if (ses.Completed == 0) continue;   // solo falliti: già avvisati
            FlushFailures(force: true);
            var name = string.IsNullOrEmpty(key.Series) ? "Download" : key.Series;
            var text = ses.Completed == 1 ? "1 file scaricato" : $"{ses.Completed} file scaricati";
            if (ses.Failed > 0) text += $", {ses.Failed} {(ses.Failed == 1 ? "fallito" : "falliti")}";
            if (CanNotify)
                Notify!(ses.Failed > 0 ? $"{name}: finito con errori" : $"{name}: completato", text,
                        ses.Failed > 0 ? WindowsNotifier.Kind.Warning : WindowsNotifier.Kind.Info);
        }
    }

    /// <summary>Una coda SFTP è finita quando non ha più lavori in corso o in attesa.</summary>
    private void CheckSftpFinished()
    {
        for (int q = 0; q < _sftp.Length; q++)
        {
            var session = _sftpSession[q];
            bool busy = false;
            foreach (var j in _sftp[q].Source())
            {
                if (j.Status == SftpJobStatus.Pending || j.IsActive)
                {
                    busy = true;
                    session.Add(j);
                }
            }
            if (busy || session.Count == 0) continue;

            int done = session.Count(j => j.Status == SftpJobStatus.Completed);
            int failed = session.Count(j => j.Status == SftpJobStatus.Failed);
            session.Clear();
            if (done + failed == 0) continue;   // tutto annullato o in pausa: niente da dire
            var (label, verb, _) = _sftp[q];
            var text = $"{done} {(done == 1 ? "file" : "file")} {verb}";
            if (failed > 0) text += $", {failed} {(failed == 1 ? "fallito" : "falliti")}";
            if (CanNotify)
                Notify!(failed > 0 ? $"{label}: finito con errori" : $"{label}: completato", text,
                        failed > 0 ? WindowsNotifier.Kind.Warning : WindowsNotifier.Kind.Info);
        }
    }

    private void OnDiskSpaceLow(string root, long free)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DateTime.Now - _lastDiskWarning < TimeSpan.FromMinutes(10)) return;
            _lastDiskWarning = DateTime.Now;
            if (NotificationsEnabled && Notify != null)   // anche con l'app in primo piano: è urgente
                Notify("Spazio su disco esaurito",
                    $"Su {root} restano {DownloadQueueService.FormatBytes(free)}: i download sono in pausa. Libera spazio e premi Riprendi.",
                    WindowsNotifier.Kind.Warning);
        });
    }
}
