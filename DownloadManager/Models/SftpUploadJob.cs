using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class SftpUploadJob : TransferJobBase
{
    [ObservableProperty] private long _uploadedBytes;

    protected override long ProgressBytes => UploadedBytes;
    protected override SftpJobStatus ActiveStatus => SftpJobStatus.Uploading;

    public string FileName => Path.GetFileName(LocalPath);

    /// <summary>Cartella locale da cui viene caricato il file.</summary>
    public string SourceFolder => Path.GetDirectoryName(LocalPath) ?? "";

    /// <summary>Cartella remota di destinazione.</summary>
    public string DestinationFolder => GetRemoteParent(RemotePath);

    partial void OnUploadedBytesChanged(long value) => NotifyProgressDependents();
}