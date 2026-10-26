using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class Season : ObservableObject
{
    [ObservableProperty] private int _number;
    [ObservableProperty] private string _title = string.Empty;
    /// <summary>Lingua della stagione quando non è tutta doppiata: "Sub ITA" o "ITA + Sub ITA" (CB01).</summary>
    [ObservableProperty] private string _languageTag = string.Empty;
    /// <summary>
    /// Versione della stagione quando il sito ne ha più di una (CB01): "ita", "sub" oppure vuoto
    /// (una sola versione). Serve a distinguere "Stagione 1 · ITA" da "Stagione 1 · Sub ITA".
    /// </summary>
    public string Variant { get; set; } = string.Empty;
    /// <summary>
    /// Serie dentro la pagina quando ce n'è più d'una (CB01, es. "BEYBLADE V-FORCE"): testo dello
    /// spoiler prima di "STAGIONE". Vuoto per le pagine con una sola serie.
    /// </summary>
    public string Group { get; set; } = string.Empty;
    /// <summary>Nome leggibile della serie del gruppo ("Beyblade V-Force", "GTO 1998").</summary>
    public string GroupTitle { get; set; } = string.Empty;
    public bool IsSubVariant => Variant == "sub" || (IsFilm && LanguageTag == "Sub ITA");
    /// <summary>Film collegato nella pagina della serie (CB01, es. spoiler "SPECIALE"): Group = link del film.</summary>
    public bool IsFilm => Variant == "film";
    public bool HasLanguageTag => LanguageTag.Length > 0;
    partial void OnLanguageTagChanged(string value) => OnPropertyChanged(nameof(HasLanguageTag));
    public ObservableCollection<StreamingEpisode> Episodes { get; set; } = new();

    public string DisplayName => string.IsNullOrEmpty(Title) ? $"Stagione {Number}" : Title;
}