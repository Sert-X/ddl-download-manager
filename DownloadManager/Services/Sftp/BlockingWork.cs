namespace DownloadManager.Services.Sftp;

/// <summary>
/// Lavori lunghi e "bloccanti" (un trasferimento WinSCP occupa il thread finché il file non è
/// finito): ognuno ha un thread tutto suo invece di uno di quelli condivisi da tutta l'app.
/// Con decine di upload/download/copie insieme, i thread condivisi finivano e tutto il resto
/// (letture delle cartelle, ricerche, salvataggi) restava in coda anche per secondi.
/// </summary>
internal static class BlockingWork
{
    private const TaskCreationOptions Options = TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach;

    public static Task Run(Action work, CancellationToken ct) =>
        Task.Factory.StartNew(work, ct, Options, TaskScheduler.Default);

    public static Task<T> Run<T>(Func<T> work, CancellationToken ct) =>
        Task.Factory.StartNew(work, ct, Options, TaskScheduler.Default);
}
