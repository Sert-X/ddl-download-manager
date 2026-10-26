using Avalonia.Data.Converters;

namespace DownloadManager.Helpers;

/// <summary>Confronto testo = parametro, per mostrare l'icona giusta di uno stato (es. badge Account).</summary>
public static class ToneConverters
{
    public static readonly IValueConverter Is =
        new FuncValueConverter<string?, string?, bool>((value, parameter) => value == parameter);
}
