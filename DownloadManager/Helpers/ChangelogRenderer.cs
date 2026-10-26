using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace DownloadManager.Helpers;

/// <summary>
/// Mostra il CHANGELOG.md come testo impaginato (versioni, sezioni, elenchi, **grassetto**, `codice`).
/// Il file è incluso nell'app, così le novità si leggono anche senza connessione.
/// </summary>
public static class ChangelogRenderer
{
    private static readonly Regex RxVersion = new(@"^##\s*\[(?<v>[^\]]+)\]\s*(?:[—–-]\s*(?<d>.+))?$", RegexOptions.Compiled);
    private static readonly Regex RxInline = new(@"(\*\*.+?\*\*|`.+?`)", RegexOptions.Compiled);

    /// <summary>Il changelog incluso nell'app ("" se manca).</summary>
    public static string LoadEmbedded()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (s == null) return "";
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch { return ""; }
    }

    /// <summary>Solo la parte di una versione ("1.3.0"); tutto il testo se non la trova.</summary>
    public static string SectionFor(string markdown, string version)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => RxVersion.Match(l.Trim()) is { Success: true } m && m.Groups["v"].Value.Trim() == version.TrimStart('v'));
        if (start < 0) return markdown;
        int end = Array.FindIndex(lines, start + 1, l => l.TrimStart().StartsWith("## "));
        return string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]);
    }

    public static Control Render(string markdown, bool showVersionHeaders = true)
    {
        var panel = new StackPanel { Spacing = 4 };
        bool firstVersion = true;

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("# ")) continue;

            var vm = RxVersion.Match(trimmed);
            if (vm.Success)
            {
                if (!showVersionHeaders) continue;
                panel.Children.Add(VersionHeader(vm.Groups["v"].Value.Trim(), vm.Groups["d"].Value.Trim(), firstVersion));
                firstVersion = false;
                continue;
            }

            if (trimmed.StartsWith("### "))
            {
                var t = new TextBlock
                {
                    Text = trimmed[4..].Trim(),
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, 12, 0, 2),
                    TextWrapping = TextWrapping.Wrap
                };
                panel.Children.Add(t);
                continue;
            }

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                int indent = line.Length - line.TrimStart().Length;
                panel.Children.Add(Bullet(trimmed[2..], indent >= 2));
                continue;
            }

            var p = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 2) };
            AddInlines(p, trimmed);
            panel.Children.Add(p);
        }
        return panel;
    }

    private static Control VersionHeader(string version, string date, bool first)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, first ? 0 : 22, 0, 0) };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3),
            Child = new TextBlock { Text = "v" + version, FontWeight = FontWeight.Bold, FontSize = 13, Foreground = Brushes.White }
        };
        pill[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppAccentBrush");
        row.Children.Add(pill);
        if (date.Length > 0)
        {
            var d = new TextBlock { Text = date, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            d[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppTextMutedBrush");
            row.Children.Add(d);
        }
        return row;
    }

    private static Control Bullet(string text, bool nested)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, *"), Margin = new Thickness(nested ? 22 : 4, 1, 0, 1) };
        var dot = new TextBlock { Text = nested ? "◦" : "•", FontSize = 12.5, Margin = new Thickness(0, 0, 8, 0) };
        dot[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppAccentBrush");
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        AddInlines(body, text);
        Grid.SetColumn(body, 1);
        grid.Children.Add(dot);
        grid.Children.Add(body);
        return grid;
    }

    private static void AddInlines(TextBlock target, string text)
    {
        target.Inlines ??= new InlineCollection();
        foreach (var part in RxInline.Split(text))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                target.Inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeight.SemiBold });
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
                target.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Consolas,Menlo,monospace") });
            else
                target.Inlines.Add(new Run(part));
        }
    }
}
