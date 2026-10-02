using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using DownloadManager.Models;
using DownloadManager.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Download;

public class DownloadQueueService
{
    private readonly DownloadRepository _repository;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DownloadQueueService> _logger;

    private readonly List<DownloadItem> _pending = new();
    private readonly HashSet<int> _pendingIds = new();
    private readonly Dictionary<int, CancellationTokenSource> _active = new();
    private readonly object _queueLock = new();

    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

    private int _maxConcurrency = 3;
    private bool _isPaused = true;
    private bool _autoStartOnFirstEnqueue = true;
    private bool _isRunning = true;

    public DownloadQueueService(
        DownloadRepository repository,
        IServiceProvider serviceProvider,
        ILogger<DownloadQueueService> logger)
    {
        _repository = repository;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public ObservableCollection<DownloadItem> AllItems { get; } = new();

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            if (value < 1) value = 1;
            if (value > 20) value = 20;
            _maxConcurrency = value;
            _logger.LogInformation("Max concurrency: {Value}", value);
            TryDispatch();
        }
    }

    public int ActiveCount { get { lock (_queueLock) return _active.Count; } }
    public int PendingCount { get { lock (_queueLock) return _pending.Count; } }
    public bool IsPaused => _isPaused;

    public event Action? QueueChanged;

    // ============================================================
    //  RESTORE / ENQUEUE
    // ============================================================

    public async Task RestoreFromDatabaseAsync()
    {
        var unfinished = await _repository.GetUnfinishedAsync();
        _logger.LogInformation("Ripristino {Count} download non completati (in pausa)", unfinished.Count);

        foreach (var item in unfinished)
        {
            item.Status = DownloadStatus.Pending;
            await AddToListAsync(item);
        }
    }

    public async Task EnqueueAsync(DownloadItem item)
    {
        if (item.Id == 0)
            await _repository.InsertAsync(item);

        await AddToListAsync(item);

        item.Status = DownloadStatus.Pending;

        lock (_queueLock)
        {
            if (_pendingIds.Add(item.Id))
                _pending.Add(item);
        }

        if (_autoStartOnFirstEnqueue)
        {
            _autoStartOnFirstEnqueue = false;
            _isPaused = false;
        }

        QueueChanged?.Invoke();
        TryDispatch();
    }

    private async Task AddToListAsync(DownloadItem item)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (!AllItems.Contains(item)) AllItems.Add(item);
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!AllItems.Contains(item)) AllItems.Add(item);
            });
        }
    }

    public bool ContainsEpisode(string seriesName, int episodeNumber)
    {
        if (string.IsNullOrEmpty(seriesName) && episodeNumber == 0)
            return false;

        return AllItems.Any(i =>
            string.Equals(i.SeriesName, seriesName, StringComparison.OrdinalIgnoreCase) &&
            i.EpisodeNumber == episodeNumber);
    }

    // ============================================================
    //  FORZA PRIORITÀ
    // ============================================================

    public void ForceQueueItem(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return;

        lock (_queueLock)
        {
            if (_active.ContainsKey(item.Id))
                return;

            _pending.RemoveAll(i => i.Id == item.Id);
            _pendingIds.Remove(item.Id);

            item.IsPriority = true;
            item.Status = DownloadStatus.Pending;
            item.PauseRequested = false;
            item.ErrorMessage = string.Empty;

            int insertAt = 0;
            while (insertAt < _pending.Count && _pending[insertAt].IsPriority)
                insertAt++;

            _pending.Insert(insertAt, item);
            _pendingIds.Add(item.Id);
        }

        _isPaused = false;
        _autoStartOnFirstEnqueue = false;

        QueueChanged?.Invoke();
        TryDispatch();
    }

    public void ForceQueueGroup(SeriesGroup group)
    {
        foreach (var item in group.Items.ToList())
        {
            if (item.Status == DownloadStatus.Completed) continue;
            if (_active.ContainsKey(item.Id)) continue;
            ForceQueueItem(item);
        }
    }

    public void UnforceQueueItem(DownloadItem item)
    {
        if (!item.IsPriority) return;

        item.IsPriority = false;

        lock (_queueLock)
        {
            if (_active.ContainsKey(item.Id))
            {
                QueueChanged?.Invoke();
                return;
            }

            int index = _pending.FindIndex(i => i.Id == item.Id);
            if (index >= 0)
            {
                _pending.RemoveAt(index);
                _pending.Add(item);
            }
        }

        QueueChanged?.Invoke();
    }

    public void UnforceQueueGroup(SeriesGroup group)
    {
        foreach (var item in group.Items.ToList())
        {
            if (item.IsPriority)
                UnforceQueueItem(item);
        }
    }

    // ============================================================
    //  START / PAUSE / CANCEL (globali)
    // ============================================================

    public void StartAll()
    {
        _isPaused = false;
        _autoStartOnFirstEnqueue = false;

        lock (_queueLock)
        {
            foreach (var item in AllItems)
            {
                if (item.Status == DownloadStatus.Paused ||
                    item.Status == DownloadStatus.Pending ||
                    item.Status == DownloadStatus.Failed)
                {
                    item.Status = DownloadStatus.Pending;
                    item.ErrorMessage = string.Empty;

                    if (!_active.ContainsKey(item.Id) && _pendingIds.Add(item.Id))
                        _pending.Add(item);
                }
            }
        }

        QueueChanged?.Invoke();
        TryDispatch();
    }

    public void PauseAll()
    {
        _isPaused = true;
        _autoStartOnFirstEnqueue = false;

        List<DownloadItem> toPause;
        lock (_queueLock)
        {
            toPause = _active.Keys
                .Select(id => AllItems.FirstOrDefault(i => i.Id == id))
                .Where(i => i != null)
                .Cast<DownloadItem>()
                .ToList();

            foreach (var item in _pending)
            {
                if (item.Status == DownloadStatus.Pending)
                    item.Status = DownloadStatus.Paused;
            }
            _pending.Clear();
            _pendingIds.Clear();
        }

        foreach (var item in toPause)
        {
            item.PauseRequested = true;
            if (_active.TryGetValue(item.Id, out var cts))
                cts.Cancel();
        }

        QueueChanged?.Invoke();
    }

    // Cancella TUTTI: attivi, pending, paused, failed → Cancelled
    public void CancelAll()
    {
        List<int> activeIds;
        List<DownloadItem> toCancel;

        lock (_queueLock)
        {
            activeIds = _active.Keys.ToList();
            toCancel = AllItems
                .Where(i => i.Status == DownloadStatus.Pending
                         || i.Status == DownloadStatus.Paused
                         || i.Status == DownloadStatus.Failed)
                .ToList();

            _pending.Clear();
            _pendingIds.Clear();
        }

        foreach (var item in toCancel)
        {
            item.PauseRequested = false;
            item.Status = DownloadStatus.Cancelled;
        }

        foreach (var id in activeIds)
        {
            if (_active.TryGetValue(id, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }

        QueueChanged?.Invoke();
    }

    public void ClearCompleted()
    {
        var toRemove = AllItems.Where(i => i.Status == DownloadStatus.Completed).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in toRemove) AllItems.Remove(item);
        });
        QueueChanged?.Invoke();
    }

    public void ClearCancelled()
    {
        List<DownloadItem> toRemove;
        lock (_queueLock)
        {
            toRemove = AllItems.Where(i => i.Status == DownloadStatus.Cancelled).ToList();
            foreach (var item in toRemove)
            {
                _pending.RemoveAll(p => p.Id == item.Id);
                _pendingIds.Remove(item.Id);
            }
        }
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in toRemove) AllItems.Remove(item);
        });
        QueueChanged?.Invoke();
    }

    public void ClearFailed()
    {
        var toRemove = AllItems.Where(i => i.Status == DownloadStatus.Failed).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in toRemove) AllItems.Remove(item);
        });
        QueueChanged?.Invoke();
    }

    public void CancelGroup(SeriesGroup group)
    {
        foreach (var item in group.Items.ToList())
            CancelItem(item);
    }

    public async Task DeleteAllAsync()
    {
        CancelAll();
        await Task.Delay(300);

        List<DownloadItem> snapshot;
        lock (_queueLock)
        {
            snapshot = AllItems.ToList();
            _pending.Clear();
            _pendingIds.Clear();
        }

        foreach (var item in snapshot)
        {
            if (item.Id > 0)
            {
                try { await _repository.DeleteAsync(item.Id); } catch { }
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() => AllItems.Clear());

        QueueChanged?.Invoke();
    }

    public async Task DeleteEverythingAsync()
    {
        CancelAll();
        await Task.Delay(300);

        List<DownloadItem> snapshot;
        lock (_queueLock)
        {
            snapshot = AllItems.ToList();
            _pending.Clear();
            _pendingIds.Clear();
        }

        foreach (var item in snapshot)
        {
            if (item.Id > 0)
            {
                try { await _repository.DeleteAsync(item.Id); } catch { }
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() => AllItems.Clear());

        QueueChanged?.Invoke();
    }

    // ============================================================
    //  PER SINGOLO ITEM
    // ============================================================

    public void PauseItem(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return;

        item.PauseRequested = true;

        if (_active.TryGetValue(item.Id, out var cts))
        {
            cts.Cancel();
        }
        else
        {
            lock (_queueLock)
            {
                _pending.RemoveAll(i => i.Id == item.Id);
                _pendingIds.Remove(item.Id);
            }
            item.Status = DownloadStatus.Paused;
        }

        QueueChanged?.Invoke();
    }

    public void ResumeItem(DownloadItem item, bool force = false)
    {
        if (item.Status == DownloadStatus.Completed) return;

        lock (_queueLock)
        {
            if (_active.ContainsKey(item.Id))
                return;
        }

        item.Status = DownloadStatus.Pending;
        item.PauseRequested = false;
        item.ErrorMessage = string.Empty;

        lock (_queueLock)
        {
            if (_pendingIds.Add(item.Id))
                _pending.Add(item);
        }

        _isPaused = false;
        _autoStartOnFirstEnqueue = false;

        QueueChanged?.Invoke();
        TryDispatch();
    }

    public void CancelItem(DownloadItem item)
    {
        item.PauseRequested = false;

        if (_active.TryGetValue(item.Id, out var cts))
        {
            cts.Cancel();
        }
        else
        {
            lock (_queueLock)
            {
                _pending.RemoveAll(i => i.Id == item.Id);
                _pendingIds.Remove(item.Id);
            }
            item.Status = DownloadStatus.Cancelled;
        }

        QueueChanged?.Invoke();
    }

    public async Task RemoveItemAsync(DownloadItem item)
    {
        if (_active.TryGetValue(item.Id, out var cts))
        {
            cts.Cancel();
            await Task.Delay(200);
        }

        lock (_queueLock)
        {
            _pending.RemoveAll(i => i.Id == item.Id);
            _pendingIds.Remove(item.Id);
        }

        if (item.Id > 0)
        {
            try { await _repository.DeleteAsync(item.Id); } catch { }
        }

        await Dispatcher.UIThread.InvokeAsync(() => AllItems.Remove(item));

        QueueChanged?.Invoke();
    }

    // ============================================================
    //  DISPATCH
    // ============================================================

    private void TryDispatch()
    {
        if (!_dispatchGate.Wait(0)) return;

        Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    DownloadItem? item = null;
                    CancellationTokenSource? cts = null;

                    lock (_queueLock)
                    {
                        if (!_isRunning) return;
                        if (_isPaused) return;
                        if (_active.Count >= _maxConcurrency) return;

                        while (_pending.Count > 0)
                        {
                            var candidate = _pending[0];
                            _pending.RemoveAt(0);

                            if (candidate.Status == DownloadStatus.Cancelled ||
                                candidate.Status == DownloadStatus.Completed ||
                                candidate.Status == DownloadStatus.Paused)
                            {
                                _pendingIds.Remove(candidate.Id);
                                continue;
                            }

                            item = candidate;
                            _pendingIds.Remove(candidate.Id);
                            break;
                        }

                        if (item == null) return;

                        cts = new CancellationTokenSource();
                        _active[item.Id] = cts;
                    }

                    _ = RunItemAsync(item!, cts!);
                }
            }
            finally
            {
                _dispatchGate.Release();
            }
        });
    }

    private async Task RunItemAsync(DownloadItem item, CancellationTokenSource cts)
    {
        try
        {
            QueueChanged?.Invoke();

            var downloadService = _serviceProvider.GetRequiredService<IDownloadService>();
            await downloadService.DownloadItemAsync(item, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (item.PauseRequested)
                    item.Status = DownloadStatus.Paused;
                else
                    item.Status = DownloadStatus.Cancelled;
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                item.Status = DownloadStatus.Failed;
                item.ErrorMessage = ex.Message;
            });
            _logger.LogError(ex, "Download fallito: {Url}", item.Url);
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                item.PauseRequested = false;
                item.IsPriority = false;
            });

            lock (_queueLock)
            {
                if (_active.TryGetValue(item.Id, out var currentCts) && currentCts == cts)
                    _active.Remove(item.Id);
            }

            try { await _repository.UpdateAsync(item); } catch { }

            QueueChanged?.Invoke();
            TryDispatch();
        }
    }
}