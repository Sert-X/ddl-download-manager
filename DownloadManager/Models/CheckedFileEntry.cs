using CommunityToolkit.Mvvm.ComponentModel;
using DownloadManager.Services.Sftp;

namespace DownloadManager.Models;

/// <summary>
/// Riga di una tabella di file (remota o locale) con gli esiti dei controlli: codec per i player
/// web (colonna Web), lingua dell'audio (colonna Lingua) e "In lettura…" durante la verifica.
/// </summary>
public abstract partial class CheckedFileEntry : ObservableObject
{
    public abstract string EntryPath { get; }
    public abstract bool EntryIsDirectory { get; }
    public abstract long EntrySize { get; }
    public abstract DateTime EntryModified { get; }

    // ---- Verifica "streaming web" (colonna Web) ----
    [ObservableProperty] private WebStreamLevel _webStreamLevel;
    /// <summary>Testo breve della colonna Web (es. "Audio AC3 (muto)", "3 non idonei").</summary>
    [ObservableProperty] private string _webStreamInfo = string.Empty;
    /// <summary>Dettaglio per il tooltip (codec e tracce).</summary>
    [ObservableProperty] private string _webStreamDetail = string.Empty;

    public bool IsWebOk => WebStreamLevel == WebStreamLevel.Ok;
    public bool IsWebWarn => WebStreamLevel == WebStreamLevel.Warning;
    public bool IsWebBad => WebStreamLevel == WebStreamLevel.Bad;
    public bool IsWebUnreadable => WebStreamLevel == WebStreamLevel.Unreadable;
    public bool IsWebStale => WebStreamLevel == WebStreamLevel.Stale;
    public bool HasWebStreamDetail => !string.IsNullOrEmpty(WebStreamDetail);

    /// <summary>Il video (o un video in questa cartella) è in lettura per la verifica.</summary>
    [ObservableProperty] private bool _isVideoChecking;
    /// <summary>Colonna Web: si mostra l'esito (non durante la lettura).</summary>
    public bool ShowWebStreamInfo => !IsVideoChecking && !string.IsNullOrEmpty(WebStreamInfo);
    partial void OnIsVideoCheckingChanged(bool value) => OnPropertyChanged(nameof(ShowWebStreamInfo));
    partial void OnWebStreamInfoChanged(string value) => OnPropertyChanged(nameof(ShowWebStreamInfo));

    // ---- Controllo lingua audio (colonna Lingua) ----
    [ObservableProperty] private WebStreamLevel _langLevel;
    /// <summary>Testo breve della colonna Lingua (es. "ITA", "Senza italiano").</summary>
    [ObservableProperty] private string _langInfo = string.Empty;
    /// <summary>Tracce audio con la loro lingua (tooltip).</summary>
    [ObservableProperty] private string _langDetail = string.Empty;

    public bool IsLangOk => LangLevel == WebStreamLevel.Ok;
    public bool IsLangWarn => LangLevel == WebStreamLevel.Warning;
    public bool IsLangBad => LangLevel == WebStreamLevel.Bad;
    public bool IsLangUnreadable => LangLevel == WebStreamLevel.Unreadable;
    public bool IsLangStale => LangLevel == WebStreamLevel.Stale;

    /// <summary>Riga rossa tenue: video non adatto al web o senza italiano.</summary>
    public bool IsAnyCheckBad => IsWebBad || IsLangBad;

    public void SetLanguage(WebStreamLevel level, string info, string detail)
    {
        LangLevel = level;
        LangInfo = info;
        LangDetail = detail;
    }

    partial void OnLangLevelChanged(WebStreamLevel value)
    {
        OnPropertyChanged(nameof(IsLangOk));
        OnPropertyChanged(nameof(IsLangWarn));
        OnPropertyChanged(nameof(IsLangBad));
        OnPropertyChanged(nameof(IsLangUnreadable));
        OnPropertyChanged(nameof(IsLangStale));
        OnPropertyChanged(nameof(IsAnyCheckBad));
    }

    public void SetWebStream(WebStreamLevel level, string info, string detail)
    {
        WebStreamLevel = level;
        WebStreamInfo = info;
        WebStreamDetail = detail;
    }

    partial void OnWebStreamLevelChanged(WebStreamLevel value)
    {
        OnPropertyChanged(nameof(IsWebOk));
        OnPropertyChanged(nameof(IsWebWarn));
        OnPropertyChanged(nameof(IsWebBad));
        OnPropertyChanged(nameof(IsWebUnreadable));
        OnPropertyChanged(nameof(IsWebStale));
        OnPropertyChanged(nameof(IsAnyCheckBad));
    }

    partial void OnWebStreamDetailChanged(string value) => OnPropertyChanged(nameof(HasWebStreamDetail));

    // ---- Durata (colonna Durata) ----
    /// <summary>Ok = durata nota, Warning = sotto la soglia ("corto"), Stale = file cambiato, None = mai letta.</summary>
    [ObservableProperty] private WebStreamLevel _durationLevel;
    /// <summary>Testo breve (es. "23:41", "3:05 · corto", "2 sotto 5 min").</summary>
    [ObservableProperty] private string _durationText = string.Empty;
    [ObservableProperty] private string _durationDetail = string.Empty;

    public bool IsDurationOk => DurationLevel == WebStreamLevel.Ok;
    public bool IsDurationShort => DurationLevel == WebStreamLevel.Warning;
    public bool IsDurationStale => DurationLevel == WebStreamLevel.Stale;

    public void SetDuration(WebStreamLevel level, string text, string detail)
    {
        DurationLevel = level;
        DurationText = text;
        DurationDetail = detail;
    }

    partial void OnDurationLevelChanged(WebStreamLevel value)
    {
        OnPropertyChanged(nameof(IsDurationOk));
        OnPropertyChanged(nameof(IsDurationShort));
        OnPropertyChanged(nameof(IsDurationStale));
    }

    /// <summary>Copia gli esiti da un'altra riga (stesso file letto di nuovo).</summary>
    public void CopyChecksFrom(CheckedFileEntry source)
    {
        SetWebStream(source.WebStreamLevel, source.WebStreamInfo, source.WebStreamDetail);
        SetLanguage(source.LangLevel, source.LangInfo, source.LangDetail);
        SetDuration(source.DurationLevel, source.DurationText, source.DurationDetail);
    }
}
