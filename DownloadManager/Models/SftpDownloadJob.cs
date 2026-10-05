using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class SftpDownloadJob : TransferJobBase
{
    [ObservableProperty] private long _downloadedBytes;

    protected override long ProgressBytes => DownloadedBytes;
    protected override SftpJobStatus ActiveStatus => SftpJobStatus.Downloading;

    public string FileName => Path.GetFileName(RemotePath);

    /// <summary>Cartella remota da cui viene scaricato il file.</summary>
    public string SourceFolder => GetRemoteParent(RemotePath);

    /// <summary>Cartella locale di destinazione.</summary>
    public string DestinationFolder => Path.GetDirectoryName(LocalPath) ?? "";

    partial void OnDownloadedBytesChanged(long value) => NotifyProgressDependents();
}