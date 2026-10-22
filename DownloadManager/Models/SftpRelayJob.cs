using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

/// <summary>
/// Copia di un file da un server SFTP a un altro. I dati passano dal PC in streaming
/// (si legge dal primo server e si scrive sul secondo nello stesso momento), senza
/// file temporanei sul disco.
/// </summary>
public partial class SftpRelayJob : TransferJobBase
{
    [ObservableProperty] private long _transferredBytes;

    protected override long ProgressBytes => TransferredBytes;
    protected override SftpJobStatus ActiveStatus => SftpJobStatus.Transferring;

    public SftpConfig SourceConfig { get; init; } = null!;
    public SftpConfig DestinationConfig { get; init; } = null!;

    /// <summary>File sul server di origine.</summary>
    public string SourcePath { get; init; } = "";

    /// <summary>File sul server di destinazione (anche in <see cref="TransferJobBase.RemotePath"/>).</summary>
    public string DestinationPath
    {
        get => RemotePath;
        init => RemotePath = value;
    }

    public string FileName => Path.GetFileName(SourcePath);

    public string SourceServer => SourceConfig.Name;
    public string DestinationServer => DestinationConfig.Name;

    public string SourceFolder => $"{SourceServer}: {GetRemoteParent(SourcePath)}";
    public string DestinationFolder => $"{DestinationServer}: {GetRemoteParent(DestinationPath)}";

    partial void OnTransferredBytesChanged(long value) => NotifyProgressDependents();
}
