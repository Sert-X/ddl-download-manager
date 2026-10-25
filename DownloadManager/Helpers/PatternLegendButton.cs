using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace DownloadManager.Helpers;

/// <summary>
/// Pulsante "?" accanto a un campo pattern: apre la legenda dei segnaposto (cosa fanno,
/// con un esempio). Cliccando una riga il segnaposto viene aggiunto al pattern.
/// <see cref="Kind"/> sceglie quali segnaposto mostrare; <see cref="Target"/> è il campo
/// (ComboBox modificabile o TextBox) in cui inserirli.
/// </summary>
public class PatternLegendButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public static readonly StyledProperty<string> KindProperty =
        AvaloniaProperty.Register<PatternLegendButton, string>(nameof(Kind), "Anime");

    public static readonly StyledProperty<Control?> TargetProperty =
        AvaloniaProperty.Register<PatternLegendButton, Control?>(nameof(Target));

    /// <summary>Anime, Movie, Series, SeasonFolder, Organize, SplitFolder.</summary>
    public string Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Control? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    private record Token(string Text, string Meaning, string Example);

    // Valori d'esempio usati in tutte le legende
    private static readonly Token[] AnimeTokens =
    {
        new("{Series}", "Nome della serie", "Frieren"),
        new("{Episode}", "Numero dell'episodio", "5"),
        new("{Episode:D2}", "Numero con 2 cifre (zeri davanti)", "05"),
        new("{Episode:D3}", "Numero con 3 cifre (zeri davanti)", "005"),
        new("{Title}", "Titolo dell'episodio", "Episodio 5"),
        new("{OriginalFileName}", "Nome del file sul sito, senza estensione", "Frieren_Ep_005_SUB_ITA"),
    };

    private static readonly Token[] MovieTokens =
    {
        new("{Series}", "Titolo del film", "Inception"),
        new("{Anno}", "Anno di uscita", "2010"),
        new("{OriginalFileName}", "Nome del file sul sito, senza estensione", "Episode_001"),
    };

    private static readonly Token[] SeriesTokens =
    {
        new("{Series}", "Nome della serie", "Breaking Bad"),
        new("{Season}", "Numero della stagione", "1"),
        new("{Season:D2}", "Stagione con 2 cifre", "01"),
        new("{Episode}", "Numero dell'episodio", "5"),
        new("{Episode:D2}", "Episodio con 2 cifre", "05"),
        new("{Episode:D3}", "Episodio con 3 cifre", "005"),
        new("{Title}", "Titolo dell'episodio", "Gray Matter"),
        new("{OriginalFileName}", "Nome del file sul sito, senza estensione", "Episode_005"),
    };

    private static readonly Token[] SeasonFolderTokens =
    {
        new("{number}", "Numero della stagione", "1"),
        new("{number:D2}", "Numero con 2 cifre", "01"),
    };

    private static readonly Token[] OrganizeTokens =
    {
        new("{Series}", "Nome della serie, ricavato dal file (o dalla cartella)", "Frieren"),
        new("{Episode}", "Numero dell'episodio (nuovo numero se riordini/unisci)", "5"),
        new("{Episode:D2}", "Numero con 2 cifre", "05"),
        new("{Episode:D3}", "Numero con 3 cifre", "005"),
        new("{Title}", "Titolo trovato nel nome del file (se c'è)", "Il ritorno"),
        new("{OriginalFileName}", "Nome attuale del file, senza estensione", "Frieren_Ep_005_SUB_ITA"),
    };

    private static readonly Token[] SplitFolderTokens =
    {
        new("{Number}", "Numero della sottocartella", "2"),
        new("{Number:D2}", "Numero con 2 cifre", "02"),
        new("{Start}", "Primo episodio della sottocartella", "26"),
        new("{End}", "Ultimo episodio della sottocartella", "50"),
    };

    public PatternLegendButton()
    {
        Classes.Add("iconCircle");
        Width = 24;
        Height = 24;
        MinWidth = 0;
        MinHeight = 0;
        Padding = new Thickness(0);
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(0, 0, 8, 0);
        ToolTip.SetTip(this, "Legenda dei segnaposto");
        Content = new TextBlock
        {
            Text = "?",
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Click += (_, _) => ShowLegend();
    }

    private void ShowLegend()
    {
        var (title, tokens, notes) = Kind switch
        {
            "Movie" => ("Segnaposto per il nome del film", MovieTokens, Array.Empty<string>()),
            "Series" => ("Segnaposto per il nome degli episodi", SeriesTokens, new[] { "Esempio: {Series} S{Season:D2}E{Episode:D2} → Breaking Bad S01E05" }),
            "SeasonFolder" => ("Segnaposto per la cartella della stagione", SeasonFolderTokens, new[] { "Lascia vuoto per non creare sottocartelle per le stagioni." }),
            "Organize" => ("Segnaposto per il nome dei file", OrganizeTokens, new[]
            {
                "Una \"/\" crea sottocartelle: {Series}/{Episode:D3} → Frieren\\005.mp4",
                "File con più episodi (es. Ep 01-02): {Episode:D2} diventa 01-02.",
            }),
            "SplitFolder" => ("Segnaposto per il nome delle sottocartelle", SplitFolderTokens, new[] { "Esempio: Puntate {Start}-{End} → Puntate 26-50" }),
            _ => ("Segnaposto per il nome dei file", AnimeTokens, new[] { "Esempio: {Series} - {Episode:D3} → Frieren - 005" }),
        };

        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold, Margin = new Thickness(6, 2, 6, 0) });
        list.Children.Add(Muted(Target != null
            ? "Clicca un segnaposto per aggiungerlo al pattern. \":D2\"/\":D3\" aggiungono zeri davanti."
            : "\":D2\"/\":D3\" aggiungono zeri davanti al numero.", new Thickness(6, 0, 6, 6)));

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.FlyoutPresenterClasses.Add("legendFlyout");

        foreach (var token in tokens)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("165, 235, 190") };
            row.Children.Add(new TextBlock
            {
                Text = token.Text,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("AppAccentBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var meaning = new TextBlock { Text = token.Meaning, TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(meaning, 1);
            row.Children.Add(meaning);
            var example = Muted("→ " + token.Example, new Thickness(10, 0, 0, 0));
            example.FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace");
            example.TextWrapping = TextWrapping.NoWrap;
            example.TextTrimming = TextTrimming.CharacterEllipsis;
            example.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(example, 2);
            row.Children.Add(example);

            var button = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 4)
            };
            button.Classes.Add("ghost");
            ToolTip.SetTip(button, Target != null ? $"Aggiungi {token.Text} al pattern" : null);
            button.Click += (_, _) =>
            {
                Insert(token.Text);
                flyout.Hide();
            };
            list.Children.Add(button);
        }

        foreach (var note in notes)
            list.Children.Add(Muted("• " + note, new Thickness(6, 6, 6, 0)));

        flyout.Content = list;
        flyout.ShowAt(this);
    }

    /// <summary>Aggiunge il segnaposto in fondo al pattern del campo collegato.</summary>
    private void Insert(string token)
    {
        switch (Target)
        {
            case ComboBox combo:
                combo.Text = (combo.Text ?? "") + token;
                break;
            case TextBox box:
                box.Text = (box.Text ?? "") + token;
                box.CaretIndex = box.Text.Length;
                box.Focus();
                break;
        }
    }

    private static TextBlock Muted(string text, Thickness margin) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("AppTextMutedBrush"),
        Margin = margin
    };

    private static IBrush? Brush(string key) =>
        Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value as IBrush
            : null;
}
