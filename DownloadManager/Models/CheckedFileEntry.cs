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
    /// <summary>File danneggiato (o cartella che ne contiene): rosso.</summary>
    public bool IsDurationBad => DurationLevel == WebStreamLevel.Bad;

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
        OnPropertyChanged(nameof(IsDurationBad));
    }

    // ---- Analisi delle cartelle: file vuoti (colonna 0 byte) e contenuto (colonna Contenuto) ----
    /// <summary>File a 0 byte, o cartella che ne contiene (anche in profondità): riga rossa.</summary>
    [ObservableProperty] private bool _hasZeroByteIssue;
    /// <summary>Cartelle: file vuoti trovati dentro (anche in profondità); null = mai analizzata.</summary>
    [ObservableProperty] private int? _zeroCount;
    /// <summary>Cartelle: "N cartelle, M file" salvato dall'analisi; vuoto = mai contata.</summary>
    [ObservableProperty] private string _contentsInfo = string.Empty;

    /// <summary>Testo della colonna 0 byte.</summary>
    public string ZeroText =>
        !EntryIsDirectory ? (HasZeroByteIssue ? "Vuoto (0 byte)" : "")
        : ZeroCount is not { } n ? ""
        : n > 0 ? (n == 1 ? "1 file vuoto" : $"{n} file vuoti")
        : HasZeroByteIssue ? "Con file vuoti"
        : "Nessuno";

    public bool IsZeroBad => HasZeroByteIssue && !string.IsNullOrEmpty(ZeroText);
    public bool IsZeroOk => EntryIsDirectory && ZeroCount == 0 && !HasZeroByteIssue;
    public bool HasZeroText => !string.IsNullOrEmpty(ZeroText);

    public string ZeroDetail =>
        !EntryIsDirectory ? (HasZeroByteIssue ? "File da 0 byte: di solito un upload o un download interrotto." : "")
        : ZeroCount is not { } n ? ""
        : n > 0 ? $"{ZeroText} in questa cartella e nelle sottocartelle analizzate.\nDi solito sono upload o download interrotti."
        : HasZeroByteIssue ? "Questa cartella contiene almeno un file da 0 byte."
        : "Nessun file vuoto in questa cartella e nelle sottocartelle analizzate.";

    /// <summary>Numero di cartelle e di file letti da <see cref="ContentsInfo"/> (null se non contati).</summary>
    public int? ContentsDirs => ParseContents().Dirs;
    public int? ContentsFiles => ParseContents().Files;
    public bool HasContents => ContentsDirs != null;
    public string ContentsDirsText => ContentsDirs?.ToString("N0") ?? "";
    public string ContentsFilesText => ContentsFiles?.ToString("N0") ?? "";
    public string ContentsDetail => ParseContents() is { Dirs: { } d, Files: { } f }
        ? $"{(d == 1 ? "1 cartella" : $"{d:N0} cartelle")} e {(f == 1 ? "1 file" : $"{f:N0} file")} " +
          "in questa cartella e nelle sottocartelle analizzate."
        : "";

    private (int? Dirs, int? Files) ParseContents()
    {
        // Formato salvato: "N cartelle, M file"
        var m = System.Text.RegularExpressions.Regex.Match(ContentsInfo ?? "", @"^(\d+) cartelle, (\d+) file");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (null, null);
    }

    partial void OnHasZeroByteIssueChanged(bool value) => NotifyZero();
    partial void OnZeroCountChanged(int? value) => NotifyZero();
    private void NotifyZero()
    {
        OnPropertyChanged(nameof(ZeroText));
        OnPropertyChanged(nameof(IsZeroBad));
        OnPropertyChanged(nameof(IsZeroOk));
        OnPropertyChanged(nameof(HasZeroText));
        OnPropertyChanged(nameof(ZeroDetail));
    }

    partial void OnContentsInfoChanged(string value)
    {
        OnPropertyChanged(nameof(ContentsDirs));
        OnPropertyChanged(nameof(ContentsFiles));
        OnPropertyChanged(nameof(HasContents));
        OnPropertyChanged(nameof(ContentsDirsText));
        OnPropertyChanged(nameof(ContentsFilesText));
        OnPropertyChanged(nameof(ContentsDetail));
    }

    /// <summary>Copia gli esiti da un'altra riga (stesso file letto di nuovo).</summary>
    public void CopyChecksFrom(CheckedFileEntry source)
    {
        SetWebStream(source.WebStreamLevel, source.WebStreamInfo, source.WebStreamDetail);
        SetLanguage(source.LangLevel, source.LangInfo, source.LangDetail);
        SetDuration(source.DurationLevel, source.DurationText, source.DurationDetail);
        HasZeroByteIssue = source.HasZeroByteIssue;
        ZeroCount = source.ZeroCount;
        ContentsInfo = source.ContentsInfo;
    }
}
