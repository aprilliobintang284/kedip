using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Interop;

namespace Kedip.DockBar;

/// <summary>Every installed app (the shell's AppsFolder) with instant search; click to open, right-click to pin.</summary>
public partial class AppsPanelWindow : GlassPanel
{
    private static readonly string[] JunkWords =
        ["uninstall", "hapus instalan", "readme", "release notes", "documentation", "reset preferences", "website", "help"];

    private static readonly string[] DocumentExtensions = [".txt", ".pdf", ".chm", ".htm", ".html", ".url", ".rtf", ".md"];
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static List<(string Name, string Id)>? _catalog;
    private static long _catalogAt;

    private readonly AppServices _services;
    private readonly List<Cell> _cells = [];

    internal AppsPanelWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        Search.TextChanged += (_, _) => ApplyFilter();
        Search.PreviewKeyDown += OnSearchKey;
        WindowsStartButton.Click += (_, _) =>
        {
            CloseSmooth();
            WinKeys.Win();
        };
        SettingsButton.Click += (_, _) =>
        {
            CloseSmooth();
            SystemActions.Open("ms-settings:");
        };
        PowerButton.Click += (_, _) =>
        {
            var menu = QuickMenu.Power(this);
            menu.PlacementTarget = PowerButton;
            menu.Placement = PlacementMode.Top;
            menu.IsOpen = true;
        };
        Loaded += (_, _) =>
        {
            Search.Focus();
            Dispatcher.InvokeAsync(Populate, DispatcherPriority.Background);
        };
    }

    private sealed record Cell(Button Button, Image Icon, Border Placeholder, string Name, string Id);

    private void Populate()
    {
        if (_catalog is null || Environment.TickCount64 - _catalogAt > 5 * 60_000)
        {
            _catalog = ShellItems.AppsFolder()
                .Where(a => !IsJunk(a.Name, a.AppId))
                .GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => g.First())
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _catalogAt = Environment.TickCount64;
        }

        foreach (var (name, id) in _catalog)
        {
            var cell = CreateCell(name, id);
            _cells.Add(cell);
            AppGrid.Children.Add(cell.Button);
        }

        ApplyFilter();
        // Icons arrive progressively so the panel is usable at once.
        foreach (var cell in _cells)
        {
            Dispatcher.InvokeAsync(() => LoadIcon(cell), DispatcherPriority.Background);
        }
    }

    private static bool IsJunk(string name, string id) =>
        id.Contains("://", StringComparison.Ordinal)
        || DocumentExtensions.Any(e => id.EndsWith(e, StringComparison.OrdinalIgnoreCase))
        || JunkWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private Cell CreateCell(string name, string id)
    {
        var placeholder = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("SurfaceBrush"),
            Child = new TextBlock
            {
                Text = name[..1].ToUpperInvariant(),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var icon = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var face = new Grid { Width = 40, Height = 40 };
        face.Children.Add(placeholder);
        face.Children.Add(icon);

        var stack = new StackPanel();
        stack.Children.Add(face);
        stack.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 86,
            MaxHeight = 32,
            Margin = new Thickness(0, 7, 0, 0),
        });

        var button = new Button { Style = (Style)FindResource("AppCell"), Content = stack, ToolTip = name };
        button.Click += (_, _) => Launch(id);
        button.ContextMenu = QuickMenu.ForApp(this, _services, id, () => Launch(id));
        return new Cell(button, icon, placeholder, name, id);
    }

    private static void LoadIcon(Cell cell)
    {
        if (!Icons.TryGetValue(cell.Id, out var image))
        {
            image = ShellItems.Image(@"shell:AppsFolder\" + cell.Id, 48);
            Icons[cell.Id] = image;
        }

        if (image is not null)
        {
            cell.Icon.Source = image;
            cell.Placeholder.Visibility = Visibility.Hidden;
        }
    }

    private void ApplyFilter()
    {
        string query = Search.Text.Trim();
        int shown = 0;
        foreach (var cell in _cells)
        {
            bool match = query.Length == 0 || cell.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || Initials(cell.Name).StartsWith(query, StringComparison.CurrentCultureIgnoreCase);
            cell.Button.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            shown += match ? 1 : 0;
        }

        SectionTitle.Text = query.Length == 0 ? "Semua app" : "Hasil pencarian";
        EmptyText.Visibility = _cells.Count > 0 && shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
    }

    private static string Initials(string name) =>
        string.Concat(name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]));

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _cells.FirstOrDefault(c => c.Button.Visibility == Visibility.Visible) is { } first)
        {
            e.Handled = true;
            Launch(first.Id);
        }
    }

    private void Launch(string id)
    {
        CloseSmooth();
        SystemActions.Open("explorer.exe", @"shell:AppsFolder\" + id);
    }
}
