using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DownloadManager.ViewModels;

/// <summary>Coda in cui è finito un trasferimento (indice della scheda SFTP: 1, 2, 3).</summary>
public enum QueueKind { Upload = 1, Download = 2, Relay = 3 }

/// <summary>
/// Avviso "in coda": quando si aggiungono upload, download o copie tra server, compare per qualche
/// secondo un avviso con quanti file sono entrati e da dove vederli, e il contatore della scheda
/// della coda pulsa. Così si capisce subito che è andato tutto in coda, senza cambiare scheda.
/// </summary>
public partial class SftpTabViewModel
{
    /// <summary>File in corso o in attesa nelle code (contatori sulle schede).</summary>
    [ObservableProperty] private int _uploadQueueCount;
    [ObservableProperty] private int _downloadQueueCount;
    [ObservableProperty] private int _relayQueueCount;
    public bool HasUploadQueueCount => UploadQueueCount > 0;
    public bool HasDownloadQueueCount => DownloadQueueCount > 0;
    public bool HasRelayQueueCount => RelayQueueCount > 0;
    partial void OnUploadQueueCountChanged(int value) => OnPropertyChanged(nameof(HasUploadQueueCount));
    partial void OnDownloadQueueCountChanged(int value) => OnPropertyChanged(nameof(HasDownloadQueueCount));
    partial void OnRelayQueueCountChanged(int value) => OnPropertyChanged(nameof(HasRelayQueueCount));

    [ObservableProperty] private bool _isQueueNoticeVisible;
    [ObservableProperty] private string _queueNoticeText = string.Empty;
    [ObservableProperty] private QueueKind _queueNoticeKind = QueueKind.Upload;

    public bool IsQueueNoticeUpload => QueueNoticeKind == QueueKind.Upload;
    public bool IsQueueNoticeDownload => QueueNoticeKind == QueueKind.Download;
    public bool IsQueueNoticeRelay => QueueNoticeKind == QueueKind.Relay;
    partial void OnQueueNoticeKindChanged(QueueKind value)
    {
        OnPropertyChanged(nameof(IsQueueNoticeUpload));
        OnPropertyChanged(nameof(IsQueueNoticeDownload));
        OnPropertyChanged(nameof(IsQueueNoticeRelay));
    }

    /// <summary>Qualcosa è entrato in una coda: la vista fa pulsare il contatore di quella scheda.</summary>
    public event Action<QueueKind>? JobsQueued;
    /// <summary>"Vedi coda" nell'avviso: la vista apre la scheda della coda.</summary>
    public event Action<QueueKind>? OpenQueueRequested;

    private int _noticeCount;
    private DispatcherTimer? _noticeHide;
    private DispatcherTimer? _noticePulse;

    /// <summary>Chiamato a ogni job aggiunto (anche dai thread delle code).</summary>
    private void OnJobQueued(QueueKind kind) => Dispatcher.UIThread.Post(() =>
    {
        // Un'altra coda mentre l'avviso è aperto: si ricomincia a contare per quella
        if (!IsQueueNoticeVisible || QueueNoticeKind != kind) _noticeCount = 0;
        _noticeCount++;
        QueueNoticeKind = kind;
        var where = kind switch
        {
            QueueKind.Upload => "di upload",
            QueueKind.Download => "di download",
            _ => "Server ↔ Server"
        };
        QueueNoticeText = _noticeCount == 1 ? $"1 file in coda {where}" : $"{_noticeCount} file in coda {where}";
        IsQueueNoticeVisible = true;

        // Si nasconde 4 secondi dopo l'ultimo file accodato (una cartella grande ne accoda tanti)
        _noticeHide ??= CreateTimer(TimeSpan.FromSeconds(4), () => IsQueueNoticeVisible = false);
        _noticeHide.Stop();
        _noticeHide.Start();

        // Un impulso solo per gruppo di file (non uno per ogni file di una cartella)
        if (_noticePulse is { IsEnabled: true }) return;
        _noticePulse ??= CreateTimer(TimeSpan.FromMilliseconds(700), () => { });
        _noticePulse.Start();
        JobsQueued?.Invoke(kind);
    });

    private static DispatcherTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer { Interval = interval };
        t.Tick += (_, _) => { t.Stop(); tick(); };
        return t;
    }

    [RelayCommand]
    private void OpenQueueFromNotice()
    {
        IsQueueNoticeVisible = false;
        OpenQueueRequested?.Invoke(QueueNoticeKind);
    }

    [RelayCommand] private void CloseQueueNotice() => IsQueueNoticeVisible = false;
}
