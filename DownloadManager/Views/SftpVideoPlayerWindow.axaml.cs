using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Avalonia;
using Renci.SshNet;

namespace DownloadManager.Views;

/// <summary>
/// MediaInput che legge i dati da uno Stream (qui: file remoto aperto via SSH.NET).
/// </summary>
public class StreamMediaInput : MediaInput
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[256 * 1024];

    public StreamMediaInput(Stream stream)
    {
        _stream = stream;
        CanSeek = stream.CanSeek;
    }

    public override bool Open(out ulong size)
    {
        try
        {
            size = (ulong)_stream.Length;
            _stream.Seek(0, SeekOrigin.Begin);
            return true;
        }
        catch
        {
            size = 0;
            return false;
        }
    }

    public override int Read(IntPtr buf, uint len)
    {
        try
        {
            int toRead = (int)Math.Min(len, (uint)_buffer.Length);
            int read = _stream.Read(_buffer, 0, toRead);
            if (read > 0)
                Marshal.Copy(_buffer, 0, buf, read);
            return read; // 0 = EOF
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SFTP Read] {ex.Message}");
            return -1;
        }
    }

    public override bool Seek(ulong offset)
    {
        try
        {
            _stream.Seek((long)offset, SeekOrigin.Begin);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SFTP Seek] {ex.Message}");
            return false;
        }
    }

    // Lo stream viene rilasciato dalla finestra, non qui.
    public override void Close() { }
}

public partial class SftpVideoPlayerWindow : Window
{
    private const int SkipSeconds = 10;

    private static readonly object InitLock = new();
    private static bool _coreInitialized;

    // Garantisce una sola finestra anteprima alla volta
    private static SftpVideoPlayerWindow? _current;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private VideoView? _videoView;
    private SftpClient? _sftp;
    private Stream? _remoteStream;
    private bool _disposed;

    private long _lengthMs;
    private bool _dragging;
    private bool _updatingSlider;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    private Slider? _seekSlider;
    private Slider? _volumeSlider;
    private Button? _playPauseButton;
    private TextBlock? _currentTimeText;
    private TextBlock? _totalTimeText;

    public SftpVideoPlayerWindow()
    {
        InitializeComponent();

        // Se esiste già una finestra anteprima aperta, la chiudo
        var previous = _current;
        _current = this;
        try { previous?.Close(); } catch { }

        _videoView = this.FindControl<VideoView>("PlayerView");
        _seekSlider = this.FindControl<Slider>("SeekSlider");
        _volumeSlider = this.FindControl<Slider>("VolumeSlider");
        _playPauseButton = this.FindControl<Button>("PlayPauseButton");
        _currentTimeText = this.FindControl<TextBlock>("CurrentTimeText");
        _totalTimeText = this.FindControl<TextBlock>("TotalTimeText");

        WireControls();

        KeyDown += OnWindowKeyDown;
    }

    // ───────────────────────── Controlli ─────────────────────────

    private void WireControls()
    {
        if (this.FindControl<Button>("BackButton") is { } back)
            back.Click += (_, _) => SkipBy(-SkipSeconds);

        if (this.FindControl<Button>("ForwardButton") is { } fwd)
            fwd.Click += (_, _) => SkipBy(SkipSeconds);

        if (_playPauseButton != null)
            _playPauseButton.Click += (_, _) => TogglePlayPause();

        if (this.FindControl<Button>("FullscreenButton") is { } fs)
            fs.Click += (_, _) => ToggleFullscreen();

        if (_volumeSlider != null)
        {
            _volumeSlider.ValueChanged += (_, e) =>
            {
                if (_mediaPlayer != null)
                    _mediaPlayer.Volume = (int)e.NewValue;
            };
        }

        if (_seekSlider != null)
        {
            // handledEventsToo = true: lo Slider "consuma" gli eventi del puntatore
            _seekSlider.AddHandler(InputElement.PointerPressedEvent,
                (_, _) => _dragging = true,
                RoutingStrategies.Tunnel, handledEventsToo: true);

            _seekSlider.AddHandler(InputElement.PointerReleasedEvent,
                (_, _) => CommitSeekFromSlider(),
                RoutingStrategies.Tunnel, handledEventsToo: true);

            _seekSlider.AddHandler(InputElement.PointerCaptureLostEvent,
                (_, _) => CommitSeekFromSlider(),
                RoutingStrategies.Tunnel, handledEventsToo: true);

            _seekSlider.ValueChanged += (_, e) =>
            {
                // Durante il trascinamento aggiorno solo il tempo mostrato
                if (!_updatingSlider && _dragging && _currentTimeText != null)
                    _currentTimeText.Text = FormatTime((long)e.NewValue);
            };
        }
    }

    private void CommitSeekFromSlider()
    {
        if (!_dragging) return;
        _dragging = false;

        if (_mediaPlayer != null && _seekSlider != null && _lengthMs > 0)
            _mediaPlayer.Time = (long)_seekSlider.Value;
    }

    private void TogglePlayPause()
    {
        if (_mediaPlayer == null) return;

        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.Pause();
        else
            _mediaPlayer.Play();
    }

    private void SkipBy(int seconds)
    {
        if (_mediaPlayer == null || _lengthMs <= 0) return;

        var target = _mediaPlayer.Time + seconds * 1000L;
        target = Math.Clamp(target, 0, Math.Max(0, _lengthMs - 500));
        _mediaPlayer.Time = target;
    }

    private void ChangeVolume(int delta)
    {
        if (_volumeSlider == null) return;
        _volumeSlider.Value = Math.Clamp(_volumeSlider.Value + delta, 0, 100);
    }

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullscreen;
        }
        else
        {
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (WindowState == WindowState.FullScreen) ToggleFullscreen();
                else Close();
                e.Handled = true;
                break;
            case Key.Space:
                TogglePlayPause();
                e.Handled = true;
                break;
            case Key.Left:
                SkipBy(-SkipSeconds);
                e.Handled = true;
                break;
            case Key.Right:
                SkipBy(SkipSeconds);
                e.Handled = true;
                break;
            case Key.Up:
                ChangeVolume(5);
                e.Handled = true;
                break;
            case Key.Down:
                ChangeVolume(-5);
                e.Handled = true;
                break;
            case Key.F:
                ToggleFullscreen();
                e.Handled = true;
                break;
        }
    }

    private static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    // ───────────────────────── Inizializzazione VLC ─────────────────────────

    private static void EnsureCoreInitialized()
    {
        lock (InitLock)
        {
            if (_coreInitialized) return;

            var libVlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");

            if (Directory.Exists(libVlcDir))
                Core.Initialize(libVlcDir);
            else
                Core.Initialize();

            _coreInitialized = true;
        }
    }

    private static Task WaitForLoadedAsync(Control control)
    {
        var tcs = new TaskCompletionSource();

        void Handler(object? s, RoutedEventArgs e)
        {
            control.Loaded -= Handler;
            tcs.TrySetResult();
        }

        control.Loaded += Handler;
        return tcs.Task;
    }

    private void SetStatus(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var statusText = this.FindControl<TextBlock>("StatusText");
            if (statusText != null) statusText.Text = text;
        });
    }

    private void HookPlayerEvents(MediaPlayer player)
    {
        player.LengthChanged += (_, e) =>
        {
            _lengthMs = e.Length;
            Dispatcher.UIThread.Post(() =>
            {
                if (_seekSlider != null)
                {
                    _updatingSlider = true;
                    _seekSlider.Maximum = Math.Max(1, e.Length);
                    _updatingSlider = false;
                }
                if (_totalTimeText != null)
                    _totalTimeText.Text = FormatTime(e.Length);
            });
        };

        player.TimeChanged += (_, e) =>
        {
            if (_dragging) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_dragging) return;
                if (_seekSlider != null)
                {
                    _updatingSlider = true;
                    _seekSlider.Value = Math.Min(e.Time, _seekSlider.Maximum);
                    _updatingSlider = false;
                }
                if (_currentTimeText != null)
                    _currentTimeText.Text = FormatTime(e.Time);
            });
        };

        player.Playing += (_, _) =>
        {
            SetStatus("In riproduzione");
            Dispatcher.UIThread.Post(() =>
            {
                if (_playPauseButton != null) _playPauseButton.Content = "Pausa";
            });
        };

        player.Paused += (_, _) =>
        {
            SetStatus("In pausa");
            Dispatcher.UIThread.Post(() =>
            {
                if (_playPauseButton != null) _playPauseButton.Content = "Play";
            });
        };

        player.EndReached += (_, _) =>
        {
            SetStatus("Riproduzione terminata");
            Dispatcher.UIThread.Post(() =>
            {
                if (_playPauseButton != null) _playPauseButton.Content = "Play";
            });
        };

        player.EncounteredError += (_, _) =>
            SetStatus("Errore riproduzione (vedi output Debug per i dettagli)");
    }

    // ───────────────────────── Avvio riproduzione ─────────────────────────

    /// <param name="sftpUrl">Formato: sftp://host[:porta]/percorso/file.mp4</param>
    public async void PlayUrl(string sftpUrl, string fileName,
        string? username = null, string? password = null)
    {
        try
        {
            var fileNameText = this.FindControl<TextBlock>("FileNameText");
            if (fileNameText != null) fileNameText.Text = fileName;
            SetStatus("Connessione SFTP...");

            var uri = new Uri(sftpUrl);
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : 22;
            var remotePath = Uri.UnescapeDataString(uri.AbsolutePath);

            if (string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                username = Uri.UnescapeDataString(parts[0]);
                if (parts.Length > 1)
                    password = Uri.UnescapeDataString(parts[1]);
            }

            await Task.Run(() =>
            {
                _sftp = new SftpClient(host, port, username ?? "", password ?? "")
                {
                    BufferSize = 256 * 1024,
                    OperationTimeout = TimeSpan.FromSeconds(30)
                };
                _sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
                _sftp.Connect();

                _remoteStream = _sftp.OpenRead(remotePath);
            });

            if (_disposed)
            {
                // Finestra chiusa durante la connessione: rilascio tutto
                try { _remoteStream?.Dispose(); } catch { }
                try { _sftp?.Dispose(); } catch { }
                return;
            }

            // Il VideoView deve essere già caricato (handle nativo creato) prima del Play,
            // altrimenti VLC apre una sua finestra separata.
            if (_videoView != null && !_videoView.IsLoaded)
                await WaitForLoadedAsync(_videoView);

            if (_disposed)
            {
                try { _remoteStream?.Dispose(); } catch { }
                try { _sftp?.Dispose(); } catch { }
                return;
            }

            EnsureCoreInitialized();

            _libVlc = new LibVLC(
                "--no-video-title-show",
                "--network-caching=3000",
                "--verbose=2");

            _libVlc.Log += (_, e) =>
                System.Diagnostics.Debug.WriteLine($"[VLC {e.Level}] {e.Module}: {e.Message}");

            _mediaPlayer = new MediaPlayer(_libVlc);
            _mediaPlayer.Volume = _volumeSlider != null ? (int)_volumeSlider.Value : 80;

            HookPlayerEvents(_mediaPlayer);

            if (_videoView != null)
                _videoView.MediaPlayer = _mediaPlayer;

            var input = new StreamMediaInput(_remoteStream!);
            var media = new Media(_libVlc, input);

            _mediaPlayer.Play(media);
            media.Dispose();
        }
        catch (Exception ex)
        {
            SetStatus($"Errore: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[SFTP/LIBVLC] {ex}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ReferenceEquals(_current, this))
            _current = null;

        if (_disposed) { base.OnClosed(e); return; }
        _disposed = true;

        try { if (_videoView != null) _videoView.MediaPlayer = null; } catch { }
        try { _mediaPlayer?.Stop(); } catch { }
        try { _mediaPlayer?.Dispose(); } catch { }
        try { _libVlc?.Dispose(); } catch { }

        try { _remoteStream?.Dispose(); } catch { }
        try { if (_sftp?.IsConnected == true) _sftp.Disconnect(); } catch { }
        try { _sftp?.Dispose(); } catch { }

        base.OnClosed(e);
    }
}