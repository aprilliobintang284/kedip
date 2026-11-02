using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kedip.Core;
using Kedip.Productivity;

namespace Kedip.Views.Panels;

/// <summary>
/// Dropped files; drag out, open, remove. Tiles scroll horizontally (smooth wheel scrolling, soft edge fades);
/// a drag-out starts after the system drag threshold.
/// </summary>
public partial class ShelfPanel : UserControl
{
    private static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
        "ScrollOffset", typeof(double), typeof(ShelfPanel),
        new PropertyMetadata(0.0, (d, e) => ((ShelfPanel)d).Scroller.ScrollToHorizontalOffset((double)e.NewValue)));

    private static readonly Duration ScrollDuration = new(TimeSpan.FromMilliseconds(240));
    private static readonly Duration RemoveDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly IEasingFunction Ease = CreateEase();
    private const double EdgeFade = 24;
    private const double WheelStep = 80;

    private readonly AppServices _services;
    private bool _subscribed;
    private ShelfItem? _pressedItem;
    private FrameworkElement? _pressedTile;
    private Point _pressPoint;
    private double _scrollTarget;
    private bool _scrollAnimating;
    private (bool Left, bool Right, double Width) _fadeKey = (false, false, -1);
    private bool? _emptyShown;

    public ShelfPanel(AppServices services)
    {
        _services = services;
        InitializeComponent();
        ItemsHost.ItemsSource = Shelf.Items;
        Loaded += OnLoaded;
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => OnShown();
    }

    /// <summary>True while a file is being dragged out of the shelf (the shell may ignore its own drop target meanwhile).</summary>
    public bool IsDraggingOut { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsDraggingOut"/> changes.</summary>
    public event EventHandler? DragOutChanged;

    private ShelfService Shelf => _services.Shelf;

    private bool Animate => _services.Settings.Current.Animations && IsVisible;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A crisp 1-device-pixel dashed outline at any DPI.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        DropZone.StrokeThickness = 1 / Math.Max(1, scale);

        if (!_subscribed)
        {
            _subscribed = true;
            Shelf.Items.CollectionChanged += OnItemsChanged;
        }

        UpdateState();
        OnShown();
    }

    private void Unsubscribe()
    {
        if (_subscribed)
        {
            _subscribed = false;
            Shelf.Items.CollectionChanged -= OnItemsChanged;
        }
    }

    private void OnShown()
    {
        if (!IsVisible)
        {
            return;
        }

        // Icons load only once the shelf is actually seen; files moved elsewhere disappear.
        Shelf.RequestIcons();
        Shelf.PruneMissing();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0)
        {
            // New drops are inserted in front: bring them into view.
            ScrollTo(0, animate: Scroller.HorizontalOffset > 0);
        }

        UpdateState();
    }

    private void UpdateState()
    {
        int count = Shelf.Items.Count;
        CountText.Text = count == 0 ? string.Empty : $"{count} item";
        ClearButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool empty = count == 0;
        if (_emptyShown == empty)
        {
            return;
        }

        bool first = _emptyShown is null;
        _emptyShown = empty;
        Scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty && !first && Animate)
        {
            EmptyState.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeDuration) { EasingFunction = Ease });
        }
    }

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShelfItem item } tile)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            ResetPress();
            Open(item);
            return;
        }

        _pressedItem = item;
        _pressedTile = tile;
        _pressPoint = e.GetPosition(this);
        tile.CaptureMouse();
    }

    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem is null || _pressedTile is null || !ReferenceEquals(sender, _pressedTile))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetPress();
            return;
        }

        Vector moved = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        ShelfItem item = _pressedItem;
        FrameworkElement tile = _pressedTile;
        ResetPress();
        DragOut(item, tile);
    }

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ResetPress();

    private void Tile_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _pressedTile))
        {
            _pressedItem = null;
            _pressedTile = null;
        }
    }

    private void ResetPress()
    {
        FrameworkElement? tile = _pressedTile;
        _pressedItem = null;
        _pressedTile = null;
        if (tile?.IsMouseCaptured == true)
        {
            tile.ReleaseMouseCapture();
        }
    }

    private void DragOut(ShelfItem item, FrameworkElement tile)
    {
        tile.BeginAnimation(OpacityProperty, null);
        tile.Opacity = 0.4;
        SetDraggingOut(true);
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Log.Warn($"Shelf drag-out failed: {ex.Message}");
        }
        finally
        {
            tile.Opacity = 1;
            SetDraggingOut(false);

            // A move elsewhere leaves a dangling reference; prune it (checked off the UI thread).
            Shelf.PruneMissing();
        }
    }

    private void SetDraggingOut(bool value)
    {
        if (IsDraggingOut != value)
        {
            IsDraggingOut = value;
            DragOutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Open(ShelfItem item)
    {
        string path = item.Path;
        Task.Run(() =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not open shelf item: {ex.Message}");
                Application.Current?.Dispatcher.InvokeAsync(Shelf.PruneMissing);
            }
        });
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShelfItem item } button)
        {
            return;
        }

        e.Handled = true;
        FrameworkElement? tile = FindTile(button);
        if (tile is null || !Animate)
        {
            Shelf.Remove(item);
            return;
        }

        // Fade and collapse the tile so its neighbours glide in instead of jumping.
        tile.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, RemoveDuration) { EasingFunction = Ease };
        var shrink = new DoubleAnimation(tile.ActualWidth, 0, RemoveDuration) { EasingFunction = Ease };
        var margin = new ThicknessAnimation(new Thickness(0), RemoveDuration) { EasingFunction = Ease };
        shrink.Completed += (_, _) => Shelf.Remove(item);
        tile.BeginAnimation(OpacityProperty, fade);
        tile.BeginAnimation(MarginProperty, margin);
        tile.BeginAnimation(WidthProperty, shrink);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (Shelf.Items.Count == 0)
        {
            return;
        }

        if (!Animate)
        {
            Shelf.Clear();
            return;
        }

        ClearButton.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(150))) { EasingFunction = Ease };
        fade.Completed += (_, _) =>
        {
            Shelf.Clear();
            ItemsHost.BeginAnimation(OpacityProperty, null);
            ItemsHost.Opacity = 1;
            ClearButton.IsHitTestVisible = true;
            ScrollTo(0, animate: false);
        };
        ItemsHost.BeginAnimation(OpacityProperty, fade);
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Scroller.ScrollableWidth <= 0)
        {
            return;
        }

        e.Handled = true;
        double from = _scrollAnimating ? _scrollTarget : Scroller.HorizontalOffset;
        ScrollTo(from - e.Delta / 120.0 * WheelStep, animate: Animate);
    }

    private void ScrollTo(double offset, bool animate)
    {
        double target = Math.Clamp(offset, 0, Math.Max(0, Scroller.ScrollableWidth));
        _scrollTarget = target;
        if (!animate)
        {
            _scrollAnimating = false;
            BeginAnimation(ScrollOffsetProperty, null);
            Scroller.ScrollToHorizontalOffset(target);
            return;
        }

        var animation = new DoubleAnimation(Scroller.HorizontalOffset, target, ScrollDuration) { EasingFunction = Ease };
        animation.Completed += (_, _) =>
        {
            if (_scrollTarget == target)
            {
                _scrollAnimating = false;
            }
        };
        _scrollAnimating = true;
        BeginAnimation(ScrollOffsetProperty, animation);
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateEdgeFade();

    /// <summary>Soft fades on the edges that have more tiles beyond them.</summary>
    private void UpdateEdgeFade()
    {
        double width = Scroller.ActualWidth;
        bool scrollable = width > 0 && Scroller.ScrollableWidth > 0.5;
        bool left = scrollable && Scroller.HorizontalOffset > 0.5;
        bool right = scrollable && Scroller.HorizontalOffset < Scroller.ScrollableWidth - 0.5;
        var key = (left, right, Math.Round(width));
        if (key == _fadeKey)
        {
            return;
        }

        _fadeKey = key;
        if (!left && !right)
        {
            Scroller.OpacityMask = null;
            return;
        }

        double edge = Math.Min(0.2, EdgeFade / width);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        mask.GradientStops.Add(new GradientStop(left ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, edge));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
        mask.GradientStops.Add(new GradientStop(right ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        Scroller.OpacityMask = mask;
    }

    private static FrameworkElement? FindTile(DependencyObject start)
    {
        for (DependencyObject? node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Name: "Tile" } tile)
            {
                return tile;
            }
        }

        return null;
    }

    private static IEasingFunction CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}
