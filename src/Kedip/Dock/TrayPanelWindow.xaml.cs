using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kedip.Interop;

namespace Kedip.DockBar;

/// <summary>The apps' tray icons (like the taskbar's hidden-icons flyout): click, double-click and right-click go to the app.</summary>
public partial class TrayPanelWindow : GlassPanel
{
    private readonly TrayService _tray;

    internal TrayPanelWindow(TrayService tray, bool replaceMode, bool taskbarShown, Action toggleTaskbar)
    {
        _tray = tray;
        InitializeComponent();

        // With "Gantikan taskbar" the real taskbar can be called back from here.
        TaskbarRow.Visibility = replaceMode ? Visibility.Visible : Visibility.Collapsed;
        TaskbarToggle.Content = taskbarShown ? "Sembunyikan taskbar Windows" : "Tampilkan taskbar Windows";
        TaskbarToggle.Click += (_, _) =>
        {
            CloseSmooth();
            toggleTaskbar();
        };

        _tray.Changed += OnTrayChanged;
        Closed += (_, _) => _tray.Changed -= OnTrayChanged;
        _tray.Prune();
        Fill();
    }

    private void OnTrayChanged(object? sender, EventArgs e) => Fill();

    private void Fill()
    {
        Icons.Children.Clear();
        foreach (var item in _tray.Visible)
        {
            Icons.Children.Add(CreateCell(item));
        }

        EmptyText.Visibility = Icons.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button CreateCell(TrayItem item)
    {
        var image = new Image { Source = item.Icon, Width = 18, Height = 18, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var cell = new Button
        {
            Style = (Style)FindResource("TrayCell"),
            Content = image,
            ToolTip = string.IsNullOrWhiteSpace(item.Title) ? null : item.Title,
        };
        System.Windows.Automation.AutomationProperties.SetName(cell, item.Title);

        // Plain mouse events (not Click) so the app sees downs, ups, double-clicks and right-clicks as Explorer sends them.
        cell.PreviewMouseDown += (_, e) =>
        {
            var at = CursorPx();
            if (e.ChangedButton == MouseButton.Left)
            {
                TrayService.Send(item, e.ClickCount == 2 ? TrayMouse.LeftDouble : TrayMouse.LeftDown, at);
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                TrayService.Send(item, TrayMouse.RightDown, at);
            }

            e.Handled = e.ChangedButton != MouseButton.Left;
        };
        cell.PreviewMouseUp += (_, e) =>
        {
            var at = CursorPx();
            switch (e.ChangedButton)
            {
                case MouseButton.Left:
                    TrayService.Send(item, TrayMouse.LeftUp, at);
                    break;
                case MouseButton.Right:
                    TrayService.Send(item, TrayMouse.RightUp, at);
                    e.Handled = true;
                    break;
                case MouseButton.Middle:
                    TrayService.Send(item, TrayMouse.MiddleUp, at);
                    break;
            }
        };
        cell.MouseEnter += (_, _) => TrayService.Send(item, TrayMouse.Move, CursorPx());
        return cell;
    }

    private static Point CursorPx() => NativeMethods.GetCursorPos(out var p) ? new Point(p.X, p.Y) : default;
}
