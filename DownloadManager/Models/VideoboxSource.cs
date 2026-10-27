using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

/// <summary>
/// Sorgente di file per Videobox Corrector: una cartella su un server SFTP e come
/// diventa link pubblico. Più sorgenti possono stare sullo stesso server (stesso profilo).
/// </summary>
public class VideoboxSourceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Profilo SFTP (tab SFTP). 0 = la connessione già aperta nella tab SFTP.</summary>
    public int ProfileId { get; set; }
    /// <summary>Cartella sul server da caricare nell'albero (viene tolta dal percorso per creare il link).</summary>
    public string RootPath { get; set; } = "";
    /// <summary>Dominio dei link generati, es. https://ichigolink.top</summary>
    public string Domain { get; set; } = "";
    /// <summary>Percorso pubblico dopo il dominio, es. "Anime SUB.ITA" (può essere vuoto).</summary>
    public string PublicPath { get; set; } = "";
}

/// <summary>Sorgente modificabile nella UI e usata dall'albero.</summary>
public partial class VideoboxSource : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayName))] private string _name = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UrlPreview))] private int _profileId;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UrlPreview), nameof(UrlPreviewSource), nameof(DisplayName))] private string _rootPath = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UrlPreview))] private string _domain = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UrlPreview))] private string _publicPath = "";

    /// <summary>
    /// Profilo scelto nel menu "Connessione SFTP". Il menu lavora su questo oggetto (non
    /// sull'Id): quando l'elenco dei profili si ricarica il menu lo azzera, e il ViewModel
    /// lo reimposta dall'Id, che resta quello salvato.
    /// </summary>
    [ObservableProperty] private SftpProfileOption? _profile;

    partial void OnProfileChanged(SftpProfileOption? value)
    {
        if (value != null && value.Id != ProfileId) ProfileId = value.Id;
    }

    /// <summary>Caricata nell'albero per la categoria corrente.</summary>
    [ObservableProperty] private bool _isEnabled = true;

    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name
        : (RootPath.TrimEnd('/').Split('/').LastOrDefault(p => p.Length > 0) ?? "Sorgente");

    public static VideoboxSource FromConfig(VideoboxSourceConfig c) => new()
    {
        Id = c.Id, Name = c.Name, ProfileId = c.ProfileId, RootPath = c.RootPath, Domain = c.Domain, PublicPath = c.PublicPath
    };

    public VideoboxSourceConfig ToConfig() => new()
    {
        Id = Id, Name = Name.Trim(), ProfileId = ProfileId, RootPath = RootPath.Trim(),
        Domain = Domain.Trim().TrimEnd('/'), PublicPath = PublicPath.Trim().Trim('/')
    };

    // ------------------------------------------------------------------
    //  Percorsi e link
    // ------------------------------------------------------------------

    /// <summary>Percorso del file relativo alla cartella della sorgente ("" se fuori).</summary>
    public string RelativePath(string fullPath)
    {
        var root = Normalize(RootPath);
        var path = Normalize(Uri.UnescapeDataString(fullPath));
        if (root.Length > 0 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            path = path.Substring(root.Length);
        return path.Trim('/');
    }

    /// <summary>Link pubblico di un file della sorgente.</summary>
    public string BuildPublicUrl(string fullSftpPath)
    {
        static string Encode(string path) =>
            string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        var baseUrl = Domain.Trim().TrimEnd('/');
        var encodedPublic = Encode(PublicPath.Trim().Trim('/'));
        var encodedPath = Encode(RelativePath(fullSftpPath));
        return string.IsNullOrEmpty(encodedPublic) ? $"{baseUrl}/{encodedPath}" : $"{baseUrl}/{encodedPublic}/{encodedPath}";
    }

    /// <summary>
    /// Chiave di confronto di un link rispetto a questa sorgente: il percorso dopo il dominio
    /// senza il percorso pubblico. null se il link non può appartenere alla sorgente.
    /// </summary>
    public string? UrlKey(string url)
    {
        string path;
        try { path = Uri.UnescapeDataString(new Uri(url).AbsolutePath); }
        catch { return null; }

        path = Normalize(path).Trim('/');
        var pub = Normalize(PublicPath).Trim('/');
        if (pub.Length > 0)
        {
            // Come prima: il percorso pubblico, se c'è nel link, si toglie
            if (path.Equals(pub, StringComparison.OrdinalIgnoreCase)) return "";
            if (path.StartsWith(pub + "/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(pub.Length + 1);
        }
        return path.ToLowerInvariant();
    }

    /// <summary>Host del dominio della sorgente (es. "data2ddl.ichigolink.top"), "" se non impostato.</summary>
    public string DomainHost
    {
        get
        {
            var d = Domain.Trim();
            if (d.Length == 0) return "";
            if (!d.Contains("://")) d = "https://" + d;
            return Uri.TryCreate(d, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
        }
    }

    /// <summary>Host di un link ("" se non è un indirizzo valido).</summary>
    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";

    /// <summary>Chiave di confronto di un file/cartella della sorgente.</summary>
    public string NodeKey(string fullPath) => RelativePath(fullPath).ToLowerInvariant();

    public string UrlPreviewSource => $"{RootPath.TrimEnd('/')}/Nome Serie/Nome Serie - Ep 01.mp4";
    public string UrlPreview => string.IsNullOrWhiteSpace(Domain)
        ? "Inserisci il dominio per vedere l'anteprima."
        : BuildPublicUrl(UrlPreviewSource);

    private static string Normalize(string p)
    {
        p = p.Replace('\\', '/');
        while (p.Contains("//")) p = p.Replace("//", "/");
        return p.TrimEnd('/');
    }
}

/// <summary>Voce della tendina "Profilo SFTP" di una sorgente.</summary>
public record SftpProfileOption(int Id, string Name);
