using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using DownloadManager.Helpers;

namespace DownloadManager.Views;

/// <summary>Finestra "Novità": tutte le versioni dal changelog incluso nell'app.</summary>
public class ChangelogDialog : Window
{
    public ChangelogDialog(string currentVersion)
    {
        Title = "Novità";
        Width = 760;
        Height = 680;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("AppBackgroundBrush");

        var markdown = ChangelogRenderer.LoadEmbedded();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 0, 0, 16) };
        var iconCircle = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23) };
        iconCircle[!Border.BackgroundProperty] = new DynamicResourceExtension("AppAccentSoftBrush");
        var icon = new Avalonia.Controls.Shapes.Path { Width = 22, Height = 22, Classes = { "icon", "accent" } };
        icon[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension("IconList");
        iconCircle.Child = icon;
        header.Children.Add(iconCircle);
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Novità", FontSize = 17, FontWeight = FontWeight.Bold });
        var sub = new TextBlock { Text = $"Versione installata: {currentVersion} · cronologia completa delle versioni", FontSize = 12 };
        sub[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("AppTextMutedBrush");
        titles.Children.Add(sub);
        header.Children.Add(titles);

        var content = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(18),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = markdown.Length == 0
                    ? new TextBlock { Text = "Changelog non disponibile in questa versione." }
                    : ChangelogRenderer.Render(markdown)
            }
        };

        var close = new Button { Content = "Chiudi", MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Classes = { "accent" } };
        close.Click += (_, _) => Close();

        var root = new DockPanel { Margin = new Thickness(22) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(close);
        root.Children.Add(content);
        Content = root;
    }

    public static Task ShowAsync(Window owner, string currentVersion) => new ChangelogDialog(currentVersion).ShowDialog(owner);
}
