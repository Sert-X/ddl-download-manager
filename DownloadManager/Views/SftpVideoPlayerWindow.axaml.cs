using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DownloadManager.Models;
using DownloadManager.Services.Sftp;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using MediaTrack = LibVLCSharp.Shared.MediaTrack;
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
        catch
        {
            return -1;   // stream chiuso (finestra chiusa) o connessione caduta
        }
    }

    public override bool Seek(ulong offset)
    {
        try
        {
            _stream.Seek((long)offset, SeekOrigin.Begin);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Lo stream viene rilasciato dalla finestra, non qui.
    public override void Close() { }
}

/// <summary>
/// Anteprima di un video (remoto via SFTP o sul PC) con LibVLC: tracce audio, sottotitoli e
/// informazioni sui codec. Tutto ciò che può bloccare (connessione, avvio di VLC, chiusura) gira
/// fuori dal thread dell'interfaccia, così la finestra resta reattiva.
/// </summary>
public partial class SftpVideoPlayerWindow : Window
{
    private const int SkipSeconds = 10;

    private static readonly object InitLock = new();
    private static bool _coreInitialized;

    // Garantisce una sola finestra anteprima alla volta
    private static SftpVideoPlayerWindow? _current;

    private readonly CancellationTokenSource _closing = new();
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private VideoView? _videoView;
    private SftpClient? _sftp;
    private Stream? _remoteStream;
    private bool _disposed;

    private long _lengthMs;
    private long _fileSize = -1;
    private bool _dragging;
    private bool _updatingSlider;
    private bool _updatingTracks;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private DispatcherTimer? _tracksRefresh;

    /// <summary>
    /// Tracce lette con ffprobe (lingua, titolo, "forced", "predefinita"): VLC non dice quali
    /// sottotitoli sono forced, i nomi delle tendine si fanno con questi dati.
    /// </summary>
    private System.Collections.Generic.List<ProbedStream>? _probed;
    private Func<string, CancellationToken, Task<System.Collections.Generic.List<ProbedStream>>>? _probe;

    /// <summary>Voce delle tendine audio/sottotitoli.</summary>
    private sealed record TrackItem(int Id, string Name)
    {
        public override string ToString() => Name;
    }

    public SftpVideoPlayerWindow()
    {
        InitializeComponent();

        // Se esiste già una finestra anteprima aperta, la chiudo
        var previous = _current;
        _current = this;
        try { previous?.Close(); } catch { }

        _videoView = PlayerView;
        WireControls();
        KeyDown += OnWindowKeyDown;

        // Le tracce arrivano a pezzi mentre VLC legge il file: si aggiornano le tendine una volta sola
        _tracksRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _tracksRefresh.Tick += (_, _) =>
        {
            _tracksRefresh.Stop();
            RefreshTracks();
        };
    }

    // ───────────────────────── Controlli ─────────────────────────

    private void WireControls()
    {
        BackButton.Click += (_, _) => SkipBy(-SkipSeconds);
        ForwardButton.Click += (_, _) => SkipBy(SkipSeconds);
        PlayPauseButton.Click += (_, _) => TogglePlayPause();
        FullscreenButton.Click += (_, _) => ToggleFullscreen();
        InfoButton.Click += (_, _) => ToggleInfo();
        CloseInfoButton.Click += (_, _) => ToggleInfo();

        VolumeSlider.ValueChanged += (_, e) =>
        {
            if (_mediaPlayer != null) _mediaPlayer.Volume = (int)e.NewValue;
        };

        AudioCombo.SelectionChanged += (_, _) =>
        {
            if (_updatingTracks || _mediaPlayer == null || AudioCombo.SelectedItem is not TrackItem t) return;
            _mediaPlayer.SetAudioTrack(t.Id);
        };
        SubtitleCombo.SelectionChanged += (_, _) =>
        {
            if (_updatingTracks || _mediaPlayer == null || SubtitleCombo.SelectedItem is not TrackItem t) return;
            _mediaPlayer.SetSpu(t.Id);
        };

        // handledEventsToo = true: lo Slider "consuma" gli eventi del puntatore
        SeekSlider.AddHandler(InputElement.PointerPressedEvent,
            (_, _) => _dragging = true,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.AddHandler(InputElement.PointerReleasedEvent,
            (_, _) => CommitSeekFromSlider(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.AddHandler(InputElement.PointerCaptureLostEvent,
            (_, _) => CommitSeekFromSlider(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.ValueChanged += (_, e) =>
        {
            // Durante il trascinamento aggiorno solo il tempo mostrato
            if (!_updatingSlider && _dragging)
                CurrentTimeText.Text = FormatTime((long)e.NewValue);
        };
    }

    private void CommitSeekFromSlider()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_mediaPlayer != null && _lengthMs > 0)
            _mediaPlayer.Time = (long)SeekSlider.Value;
    }

    private void TogglePlayPause()
    {
        if (_mediaPlayer == null) return;
        if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
        else _mediaPlayer.Play();
    }

    private void SkipBy(int seconds)
    {
        if (_mediaPlayer == null || _lengthMs <= 0) return;
        var target = _mediaPlayer.Time + seconds * 1000L;
        _mediaPlayer.Time = Math.Clamp(target, 0, Math.Max(0, _lengthMs - 500));
    }

    private void ChangeVolume(int delta) =>
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
            WindowState = _stateBeforeFullscreen;
        else
        {
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    private void ToggleInfo()
    {
        InfoPanel.IsVisible = !InfoPanel.IsVisible;
        InfoButton.Classes.Set("accent", InfoPanel.IsVisible);
        if (InfoPanel.IsVisible) BuildInfo();
    }

    /// <summary>Passa all'elemento successivo di una tendina (tasti A e S).</summary>
    private static void CycleCombo(ComboBox combo)
    {
        if (!combo.IsEnabled || combo.ItemCount < 2) return;
        combo.SelectedIndex = (combo.SelectedIndex + 1) % combo.ItemCount;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (WindowState == WindowState.FullScreen) ToggleFullscreen();
                else Close();
                break;
            case Key.Space: TogglePlayPause(); break;
            case Key.Left: SkipBy(-SkipSeconds); break;
            case Key.Right: SkipBy(SkipSeconds); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.F: ToggleFullscreen(); break;
            case Key.I: ToggleInfo(); break;
            case Key.A: CycleCombo(AudioCombo); break;
            case Key.S: CycleCombo(SubtitleCombo); break;
            default: return;
        }
        e.Handled = true;
    }

    private static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    // ───────────────────────── Stato ─────────────────────────

    private enum StatusKind { Busy, Playing, Paused, Error }

    private void SetStatus(string text, StatusKind kind = StatusKind.Busy)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            StatusText.Text = text;
            var key = kind switch
            {
                StatusKind.Playing => "AppSuccessBrush",
                StatusKind.Paused => "AppTextMutedBrush",
                StatusKind.Error => "AppDangerBrush",
                _ => "AppWarningBrush"
            };
            if (this.TryFindResource(key, ActualThemeVariant, out var brush) && brush is IBrush b)
                StatusDot.Fill = b;
        });
    }

    /// <summary>Icona del pulsante centrale: pausa mentre riproduce, play altrimenti.</summary>
    private void SetPlayPauseIcon(bool playing)
    {
        if (this.TryFindResource(playing ? "IconPause" : "IconPlay", out var geometry) && geometry is Geometry g)
            PlayPauseIcon.Data = g;
    }

    // ───────────────────────── Inizializzazione VLC ─────────────────────────

    private static void EnsureCoreInitialized()
    {
        lock (InitLock)
        {
            if (_coreInitialized) return;
            var libVlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            if (Directory.Exists(libVlcDir)) Core.Initialize(libVlcDir);
            else Core.Initialize();
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

    private void HookPlayerEvents(MediaPlayer player)
    {
        // Gli eventi di VLC arrivano dai suoi thread: qui non si chiama mai VLC, si passa
        // tutto al thread dell'interfaccia.
        player.LengthChanged += (_, e) =>
        {
            _lengthMs = e.Length;
            Dispatcher.UIThread.Post(() =>
            {
                _updatingSlider = true;
                SeekSlider.Maximum = Math.Max(1, e.Length);
                _updatingSlider = false;
                TotalTimeText.Text = FormatTime(e.Length);
            });
        };

        player.TimeChanged += (_, e) =>
        {
            if (_dragging) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_dragging || _disposed) return;
                _updatingSlider = true;
                SeekSlider.Value = Math.Min(e.Time, SeekSlider.Maximum);
                _updatingSlider = false;
                CurrentTimeText.Text = FormatTime(e.Time);
            });
        };

        player.Playing += (_, _) =>
        {
            SetStatus("In riproduzione", StatusKind.Playing);
            Dispatcher.UIThread.Post(() =>
            {
                SetPlayPauseIcon(playing: true);
                ScheduleTracksRefresh();
            });
        };
        player.Paused += (_, _) =>
        {
            SetStatus("In pausa", StatusKind.Paused);
            Dispatcher.UIThread.Post(() => SetPlayPauseIcon(playing: false));
        };
        player.EndReached += (_, _) =>
        {
            SetStatus("Riproduzione terminata", StatusKind.Paused);
            Dispatcher.UIThread.Post(() => SetPlayPauseIcon(playing: false));
        };
        player.EncounteredError += (_, _) =>
            SetStatus("Errore di riproduzione: il file non si apre o la connessione si è interrotta", StatusKind.Error);

        player.ESAdded += (_, _) => Dispatcher.UIThread.Post(ScheduleTracksRefresh);
        player.ESDeleted += (_, _) => Dispatcher.UIThread.Post(ScheduleTracksRefresh);
        player.ESSelected += (_, _) => Dispatcher.UIThread.Post(ScheduleTracksRefresh);
    }

    private void ScheduleTracksRefresh()
    {
        if (_disposed || _tracksRefresh == null) return;
        _tracksRefresh.Stop();
        _tracksRefresh.Start();
    }

    // ───────────────────────── Tracce e informazioni ─────────────────────────

    private void RefreshTracks()
    {
        if (_disposed || _mediaPlayer == null) return;
        _updatingTracks = true;
        try
        {
            var probedAudio = _probed?.Where(p => p.Type == "audio").ToList();
            var probedSubs = _probed?.Where(p => p.Type == "subtitle").ToList();

            int n = 0;
            var audio = (_mediaPlayer.AudioTrackDescription ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>())
                .Select(t => t.Id < 0
                    ? new TrackItem(t.Id, "Disattivato")
                    : new TrackItem(t.Id, Label(probedAudio, n++, t.Name, audio: true)))
                .ToList();
            FillCombo(AudioCombo, audio, _mediaPlayer.AudioTrack);

            n = 0;
            var subs = (_mediaPlayer.SpuDescription ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>())
                .Select(t => t.Id < 0
                    ? new TrackItem(t.Id, "Nessuno")
                    : new TrackItem(t.Id, Label(probedSubs, n++, t.Name, audio: false)))
                .ToList();
            if (subs.Count == 0) subs.Add(new TrackItem(-1, "Nessuno"));
            FillCombo(SubtitleCombo, subs, _mediaPlayer.Spu);
        }
        catch { /* il video si sta chiudendo */ }
        finally { _updatingTracks = false; }

        if (InfoPanel.IsVisible) BuildInfo();
    }

    private static string CleanName(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

    /// <summary>
    /// Nome di una traccia nelle tendine: lingua per esteso, titolo, "(forced)" e per l'audio il
    /// formato. Es. "Italiano (forced)", "Inglese · English Forced", "Italiano · AC3 5.1".
    /// Senza dati di ffprobe si usa il nome di VLC.
    /// </summary>
    private static string Label(System.Collections.Generic.List<ProbedStream>? probed, int index, string? vlcName, bool audio)
    {
        if (probed == null || index >= probed.Count)
            return CleanName(vlcName, audio ? "Traccia" : "Sottotitoli");
        var p = probed[index];
        var name = LanguageRules.IsUndefined(p.Language)
            ? (audio ? $"Traccia {index + 1}" : $"Sottotitoli {index + 1}")
            : LanguageRules.Name(p.Language);
        var parts = new System.Collections.Generic.List<string> { name };
        var title = (p.Title ?? "").Trim();
        if (title.Length > 0 && !title.Equals(name, StringComparison.OrdinalIgnoreCase)
            && !title.Equals(p.Language, StringComparison.OrdinalIgnoreCase))
            parts.Add(title);
        if (audio)
        {
            var fmt = WebStreamRules.Pretty(p.Codec);
            var ch = p.Channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", > 0 => $"{p.Channels} canali", _ => "" };
            parts.Add(ch.Length > 0 ? $"{fmt} {ch}" : fmt);
        }
        var label = string.Join(" · ", parts);
        if (p.IsForced && !title.Contains("forced", StringComparison.OrdinalIgnoreCase)) label += " (forced)";
        return label;
    }

    /// <summary>Legge le tracce con ffprobe in sottofondo, poi aggiorna le tendine.</summary>
    private async Task LoadTrackDetailsAsync()
    {
        if (_probe == null) return;
        try
        {
            var ffmpeg = App.Services?.GetService(typeof(DownloadManager.Services.Download.FfmpegLocator))
                as DownloadManager.Services.Download.FfmpegLocator;
            var ffmpegPath = ffmpeg?.GetFfmpegPath();
            if (ffmpegPath == null) return;
            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
            if (!File.Exists(ffprobe)) return;

            var streams = await Task.Run(() => _probe(ffprobe, _closing.Token));
            if (_disposed) return;
            Dispatcher.UIThread.Post(() =>
            {
                _probed = streams;
                ScheduleTracksRefresh();
            });
        }
        catch { /* senza i dettagli restano i nomi di VLC */ }
    }

    private static void FillCombo(ComboBox combo, System.Collections.Generic.List<TrackItem> items, int selectedId)
    {
        combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(i => i.Id == selectedId) ?? items.FirstOrDefault();
        // Una sola voce reale (o nessuna): non c'è niente da scegliere
        combo.IsEnabled = items.Count(i => i.Id >= 0) > (combo.Name == "AudioCombo" ? 1 : 0);
    }

    /// <summary>Pannello informazioni: codec, risoluzione, tracce audio e sottotitoli.</summary>
    private void BuildInfo()
    {
        InfoContent.Children.Clear();
        MediaTrack[] tracks;
        try { tracks = _media?.Tracks ?? Array.Empty<MediaTrack>(); }
        catch { tracks = Array.Empty<MediaTrack>(); }

        var file = new System.Collections.Generic.List<(string, string)> { ("Nome", FileNameText.Text ?? "") };
        if (_fileSize > 0) file.Add(("Dimensione", SftpRemoteEntry.FormatSize(_fileSize)));
        if (_lengthMs > 0) file.Add(("Durata", FormatTime(_lengthMs)));
        AddSection("FILE", file);

        if (tracks.Length == 0)
        {
            InfoContent.Children.Add(new TextBlock
            {
                Text = "Le tracce compaiono quando il video parte.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = this.TryFindResource("AppTextMutedBrush", ActualThemeVariant, out var b) ? b as IBrush : null
            });
            return;
        }

        int n = 0;
        foreach (var t in tracks.Where(t => t.TrackType == TrackType.Video))
        {
            var v = t.Data.Video;
            var rows = new System.Collections.Generic.List<(string, string)> { ("Codec", Codec(t)) };
            if (v.Width > 0) rows.Add(("Risoluzione", $"{v.Width} × {v.Height}"));
            if (v.FrameRateNum > 0 && v.FrameRateDen > 0) rows.Add(("Fotogrammi", $"{(double)v.FrameRateNum / v.FrameRateDen:0.###} fps"));
            if (t.Bitrate > 0) rows.Add(("Bitrate", $"{t.Bitrate / 1000} kb/s"));
            AddSection(++n == 1 ? "VIDEO" : $"VIDEO {n}", rows);
        }

        n = 0;
        foreach (var t in tracks.Where(t => t.TrackType == TrackType.Audio))
        {
            var a = t.Data.Audio;
            var rows = new System.Collections.Generic.List<(string, string)> { ("Codec", Codec(t)) };
            if (!string.IsNullOrWhiteSpace(t.Language)) rows.Add(("Lingua", LanguageRules.Name(t.Language)));
            if (!string.IsNullOrWhiteSpace(t.Description)) rows.Add(("Titolo", t.Description));
            if (a.Channels > 0) rows.Add(("Canali", a.Channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => a.Channels.ToString() }));
            if (a.Rate > 0) rows.Add(("Frequenza", $"{a.Rate / 1000.0:0.#} kHz"));
            AddSection($"AUDIO {++n}", rows);
        }

        n = 0;
        var probedSubs = _probed?.Where(p => p.Type == "subtitle").ToList();
        foreach (var t in tracks.Where(t => t.TrackType == TrackType.Text))
        {
            var rows = new System.Collections.Generic.List<(string, string)> { ("Formato", Codec(t)) };
            if (!string.IsNullOrWhiteSpace(t.Language)) rows.Add(("Lingua", LanguageRules.Name(t.Language)));
            if (!string.IsNullOrWhiteSpace(t.Description)) rows.Add(("Titolo", t.Description));
            if (probedSubs != null && n < probedSubs.Count)
            {
                var p = probedSubs[n];
                if (p.IsForced) rows.Add(("Forced", "sì (solo le parti in altra lingua)"));
                if (p.IsDefault) rows.Add(("Predefinita", "sì"));
            }
            AddSection($"SOTTOTITOLI {++n}", rows);
        }
    }

    private string Codec(MediaTrack t)
    {
        string? name = null;
        try { name = _media?.CodecDescription(t.TrackType, t.Codec); } catch { }
        var fourcc = FourCC(t.Codec);
        return string.IsNullOrWhiteSpace(name) ? fourcc : $"{name} ({fourcc})";
    }

    private static string FourCC(uint codec)
    {
        var chars = new[] { (char)(codec & 0xFF), (char)((codec >> 8) & 0xFF), (char)((codec >> 16) & 0xFF), (char)((codec >> 24) & 0xFF) };
        return new string(chars).Trim('\0', ' ');
    }

    private void AddSection(string title, System.Collections.Generic.List<(string Key, string Value)> rows)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = title, Classes = { "infoTitle" } });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90, *"), RowSpacing = 4 };
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var key = new TextBlock { Text = rows[i].Key, Classes = { "infoKey" } };
            var value = new TextBlock { Text = rows[i].Value, Classes = { "infoValue" } };
            Grid.SetRow(key, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(key);
            grid.Children.Add(value);
        }
        panel.Children.Add(grid);
        InfoContent.Children.Add(new Border
        {
            Child = panel,
            Padding = new Avalonia.Thickness(12, 10),
            CornerRadius = new Avalonia.CornerRadius(10),
            Background = this.TryFindResource("AppSurface2Brush", ActualThemeVariant, out var bg) ? bg as IBrush : null,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
    }

    // ───────────────────────── Avvio riproduzione ─────────────────────────

    /// <summary>Anteprima di un file sul PC (stesso player dei file remoti).</summary>
    public void PlayLocalFile(string localPath)
    {
        _probe = (ffprobe, ct) => WebStreamCheckerService.ProbeLocalStreamsAsync(ffprobe, localPath, ct);
        try { _fileSize = new FileInfo(localPath).Length; } catch { }
        PlayFrom(Path.GetFileName(localPath), "Apertura file...", _ =>
            Task.FromResult<Stream>(new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 256 * 1024)));
    }

    /// <summary>Anteprima di un file sul server (password o chiave, come il profilo).</summary>
    public void PlayRemote(SftpConfig server, string remotePath, string fileName, long size = -1)
    {
        _fileSize = size;
        _probe = (ffprobe, ct) => _sftp == null
            ? Task.FromResult(new System.Collections.Generic.List<ProbedStream>())
            : WebStreamCheckerService.ProbeRemoteStreamsAsync(_sftp, ffprobe, remotePath, size, ct);
        PlayFrom(fileName, $"Connessione a {server.Name}...", async ct =>
        {
            var client = await SshNetConnector.ConnectAsync(server, ct);
            _sftp = client;
            SetStatus("Apertura del file...");
            return await Task.Run(() => (Stream)client.OpenRead(remotePath), ct);
        });
    }

    /// <summary>Apre lo stream e avvia VLC, tutto fuori dal thread dell'interfaccia.</summary>
    private async void PlayFrom(string fileName, string openingStatus, Func<CancellationToken, Task<Stream>> openStream)
    {
        var ct = _closing.Token;
        try
        {
            FileNameText.Text = fileName;
            Title = $"Anteprima · {fileName}";
            SetStatus(openingStatus);

            _remoteStream = await openStream(ct);
            if (_disposed) return;   // la pulizia la fa la chiusura

            // Il VideoView deve essere già caricato (handle nativo creato) prima del Play,
            // altrimenti VLC apre una sua finestra separata.
            if (_videoView != null && !_videoView.IsLoaded)
                await WaitForLoadedAsync(_videoView);
            if (_disposed) return;

            SetStatus("Avvio del lettore...");
            // Creare LibVLC carica i plugin: qualche centinaio di ms, meglio non sull'interfaccia
            var stream = _remoteStream;
            var (libVlc, player, media) = await Task.Run(() =>
            {
                EnsureCoreInitialized();
                var lib = new LibVLC("--no-video-title-show", "--network-caching=3000", "--quiet");
                var mp = new MediaPlayer(lib);
                var m = new Media(lib, new StreamMediaInput(stream!));
                return (lib, mp, m);
            }, ct);
            _libVlc = libVlc;
            _mediaPlayer = player;
            _media = media;
            if (_disposed) return;

            _mediaPlayer.Volume = (int)VolumeSlider.Value;
            HookPlayerEvents(_mediaPlayer);
            if (_videoView != null) _videoView.MediaPlayer = _mediaPlayer;

            SetStatus("Caricamento del video...");
            _mediaPlayer.Play(_media);
            _ = LoadTrackDetailsAsync();
        }
        catch (OperationCanceledException) { /* finestra chiusa durante l'apertura */ }
        catch (Exception ex)
        {
            if (_disposed) return;
            SetStatus($"Errore: {ex.Message}", StatusKind.Error);
            System.Diagnostics.Debug.WriteLine($"[ANTEPRIMA] {ex}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ReferenceEquals(_current, this))
            _current = null;

        if (_disposed) { base.OnClosed(e); return; }
        _disposed = true;
        _tracksRefresh?.Stop();

        // Interrompe una connessione in corso
        try { _closing.Cancel(); } catch { }
        try { if (_videoView != null) _videoView.MediaPlayer = null; } catch { }

        // Stop e rilascio di VLC e SSH possono bloccare anche secondi (lettura ferma sulla rete):
        // si fanno in sottofondo, la finestra si chiude subito.
        var stream = _remoteStream;
        var player = _mediaPlayer;
        var media = _media;
        var lib = _libVlc;
        var sftp = _sftp;
        _ = Task.Run(async () =>
        {
            // Lo stream si chiude per primo: sblocca VLC se è fermo in una lettura
            try { stream?.Dispose(); } catch { }
            try { player?.Stop(); } catch { }
            try { media?.Dispose(); } catch { }
            try { player?.Dispose(); } catch { }
            try { lib?.Dispose(); } catch { }
            try { sftp?.Dispose(); } catch { }
            // La connessione potrebbe essere arrivata dopo la chiusura
            await Task.Delay(200);
            try { if (!ReferenceEquals(_sftp, sftp)) _sftp?.Dispose(); } catch { }
            try { if (!ReferenceEquals(_remoteStream, stream)) _remoteStream?.Dispose(); } catch { }
        });

        base.OnClosed(e);
    }
}
