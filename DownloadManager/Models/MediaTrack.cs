using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public enum MediaTrackKind { Audio, Subtitle }

/// <summary>Traccia audio o sottotitoli dichiarata nella playlist master HLS (#EXT-X-MEDIA).</summary>
public partial class MediaTrack : ObservableObject
{
    public MediaTrackKind Kind { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Attributo LANGUAGE della playlist (es. "ita", "eng"). Può essere vuoto.</summary>
    public string Language { get; init; } = "";
    public string Url { get; init; } = "";
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>Identificativo salvato nel download: la lingua se c'è, altrimenti il nome.</summary>
    public string Key => !string.IsNullOrWhiteSpace(Language) ? Language : Name;

    public string DisplayName
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(Name) ? Language : Name;
            if (!string.IsNullOrWhiteSpace(Language) && !label.Contains(Language, StringComparison.OrdinalIgnoreCase))
                label += $" ({Language})";
            if (IsForced) label += " · forzati";
            return label;
        }
    }

    /// <summary>Nome mostrato nella riga (il codice lingua va nel badge a parte).</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? Language : Name;
    public bool HasLanguage => !string.IsNullOrWhiteSpace(Language);

    /// <summary>true se la traccia è in italiano (lingua "it"/"ita" o nome che lo dice).</summary>
    public bool IsItalian =>
        Language.StartsWith("it", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("ital", StringComparison.OrdinalIgnoreCase) ||
        Name.Equals("ita", StringComparison.OrdinalIgnoreCase);

    /// <summary>Corrispondenza con un identificativo salvato (lingua o nome, senza distinzione di maiuscole).</summary>
    public bool Matches(string key) =>
        string.Equals(Key, key, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Language, key, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, key, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Contenuto di una playlist master: varianti video e tracce alternative.</summary>
public class MasterTracks
{
    public List<VideoQuality> Videos { get; } = new();
    public List<MediaTrack> Audios { get; } = new();
    public List<MediaTrack> Subtitles { get; } = new();
}
