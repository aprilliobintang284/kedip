using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Mascot;
using Kedip.Shell.Island;
using Microsoft.Win32;

namespace Kedip.Shell;

/// <summary>
/// The notch overlay window. See DESIGN.md "Notch shell".
/// States (high → low): Hidden › Expanded › Peek › Collapsed. The window itself never moves or resizes after anchoring;
/// only the island inside it animates.
/// </summary>
public partial class NotchWindow : Window
{
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(150);

    // Tucked: a thin line over a maximized browser so its tabs stay usable. Touching it reveals the notch at once;
    // it opens only after a slightly longer hover, so sweeping across the tab strip doesn't pop it open.
    private static readonly TimeSpan TuckedHoverDelay = TimeSpan.FromMilliseconds(400);
    private const double TuckHeight = 4;
    private const double TuckWaveHeight = 14;
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DefaultPeekDuration = TimeSpan.FromSeconds(3);

    private readonly AppServices _services;
    private readonly CollapsedView _collapsed;
    private readonly PeekView[] _peekViews;
    private readonly PeekQueue _peekQueue = new();
    private readonly MinuteClock _clock = new();
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _leaveTimer;
    private readonly DispatcherTimer _peekTimer;

    private ExpandedView? _expanded;
    private IntPtr _hwnd;
    private IslandMode _mode = IslandMode.Collapsed;
    private CollapsedContent? _lastContent;
    private PeekRequest? _currentPeek;
    private Size _peekSize;
    private int _peekFront;
    private IslandTab _lastTab;
    private bool _isExpanded;
    private bool _isHidden;
    private bool _isTucked;
    private TuckLook _tuckLook;
    private bool _revealing;
    private readonly BeatWave _wave;
    private ImageSource? _musicLineArt;
    private bool _hoverPending;
    private bool _suppressHover;
    private bool _dragInside;
    private bool _dragHasNewFiles;
    private bool _introDone;

    public NotchWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _collapsed = new CollapsedView();
        IslandSurface.SetStretch(_collapsed, true);
        _wave = new BeatWave { Visibility = Visibility.Collapsed };
        IslandSurface.SetStretch(_wave, true);
        _peekViews = [new PeekView(), new PeekView()];
        Surface.Children.Add(_collapsed);
        Surface.Children.Add(_wave);
        Surface.Children.Add(_peekViews[0]);
        Surface.Children.Add(_peekViews[1]);

        _hoverTimer = CreateTimer(HoverDelay, OnHoverElapsed);
        _leaveTimer = CreateTimer(LeaveDelay, OnLeaveElapsed);
        _peekTimer = CreateTimer(DefaultPeekDuration, (_, _) => EndPeek());

        var settings = services.Settings.Current;
        _lastTab = settings.PrayerEnabled ? IslandTab.Prayer : IslandTab.Timer;

        // Start as a sliver so the first layout can drop the island in from the top edge.
        var (baseWidth, baseHeight) = settings.GetNotchSize();
        Surface.Anchor = settings.Position;
        Surface.EarRadius = NotchShape.EarRadiusFor(baseHeight);
        Surface.BodyWidth = settings.Animations ? Math.Round(baseWidth * 0.6) : baseWidth;
        Surface.BodyHeight = settings.Animations ? 0 : baseHeight;

        Surface.MouseEnter += OnIslandMouseEnter;
        Surface.MouseLeave += OnIslandMouseLeave;
        Surface.MouseLeftButtonUp += OnIslandClick;
        Surface.AddHandler(DragEnterEvent, new DragEventHandler(OnIslandDragOver), handledEventsToo: true);
        Surface.AddHandler(DragOverEvent, new DragEventHandler(OnIslandDragOver), handledEventsToo: true);
        Surface.AddHandler(DragLeaveEvent, new DragEventHandler(OnIslandDragLeave), handledEventsToo: true);
        Surface.AddHandler(DropEvent, new DragEventHandler(OnIslandDrop), handledEventsToo: true);

        services.Settings.Changed += OnSettingsChanged;
        services.Peeks.Requested += OnPeekRequested;
        services.Foreground.Changed += OnForegroundChanged;
        services.Mascot.Changed += OnContentChanged;
        services.Mascot.LookChanged += OnMascotLook;
        services.Mascot.Woke += OnMascotWoke;
        services.Media.TrackChanged += OnContentChanged;
        services.Media.PlaybackChanged += OnContentChanged;
        services.Lyrics.LyricsChanged += OnContentChanged;
        services.Lyrics.LineChanged += OnContentChanged;
        services.Power.Changed += OnContentChanged;
        _clock.Changed += OnClockChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.TimeChanged += OnSystemTimeChanged;
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(Anchor, DispatcherPriority.Loaded);
        Loaded += OnLoaded;
        Closed += OnClosed;

        _isHidden = IsHiddenWanted();
        Refresh(animate: false);
        if (settings.Animations)
        {
            _collapsed.Opacity = 0;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        NotchPlacement.ApplyWindowStyles(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Anchor();
    }

    private static DispatcherTimer CreateTimer(TimeSpan interval, EventHandler tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = interval };
        timer.Tick += tick;
        return timer;
    }

    private bool Animations => _services.Settings.Current.Animations;

    // ---- State -------------------------------------------------------------------------------------------------

    /// <summary>Brings every layer, timer and the island size in line with the current state. Cheap and idempotent.</summary>
    private void Refresh(bool animate = true)
    {
        animate &= Animations && _introDone;
        var settings = _services.Settings.Current;
        var (baseWidth, baseHeight) = settings.GetNotchSize();
        Surface.Anchor = settings.Position;
        _collapsed.Eyes.Animate = settings.Animations;

        var mode = _isExpanded ? IslandMode.Expanded : _currentPeek is not null ? IslandMode.Peek : IslandMode.Collapsed;
        var previousMode = _mode;
        _mode = mode;
        var content = CollapsedContent.From(_services);
        _tuckLook = ComputeTuckLook(mode, content);
        _isTucked = _tuckLook is TuckLook.Line or TuckLook.Wave;
        Surface.EarRadius = _isTucked ? (_tuckLook == TuckLook.Wave ? 3 : 2) : NotchShape.EarRadiusFor(baseHeight);
        UpdateMusicLine(content, baseHeight);

        Size target;
        bool calm = false;
        switch (mode)
        {
            case IslandMode.Expanded:
                target = new Size(ExpandedView.BodyWidth, ExpandedView.BodyHeight);
                break;
            case IslandMode.Peek:
                _peekSize = _peekViews[_peekFront].Prepare(_currentPeek!, baseWidth, baseHeight);
                target = _peekSize;
                break;
            default:
                double width = _collapsed.Apply(content, baseWidth, baseHeight, animate && previousMode == IslandMode.Collapsed,
                    hideCentre: _tuckLook == TuckLook.MiniWave);
                target = _tuckLook switch
                {
                    TuckLook.Wave => new Size(Math.Max(100, Math.Round(baseWidth * 0.72)), TuckWaveHeight),
                    TuckLook.Line => new Size(Math.Max(56, Math.Round(baseWidth * 0.42)), TuckHeight),
                    _ => new Size(width, baseHeight),
                };
                calm = previousMode == IslandMode.Collapsed && content.Centre == CentreContent.Lyric &&
                       _lastContent?.Centre == CentreContent.Lyric && _lastContent?.HasWings == content.HasWings;
                break;
        }

        _lastContent = content;
        if (mode != IslandMode.Expanded && _hoverPending)
        {
            // Hover feedback: the island leans out a touch before it opens.
            target = new Size(target.Width + 8, target.Height + 2);
        }

        bool collapsedVisible = mode == IslandMode.Collapsed && !_isTucked;
        Motion.FadeTo(_collapsed, collapsedVisible, animate, collapsedVisible ? TimeSpan.FromMilliseconds(180) : TimeSpan.FromMilliseconds(90),
            collapsedVisible && previousMode != IslandMode.Collapsed ? TimeSpan.FromMilliseconds(90) : null);
        if (mode != IslandMode.Peek)
        {
            _peekViews[0].Leave(animate);
            _peekViews[1].Leave(animate);
        }

        if (mode != IslandMode.Expanded && _expanded is { IsOpen: true })
        {
            _expanded.Close(animate);
        }

        if (_introDone)
        {
            Surface.ResizeTo(target.Width, target.Height, animate, calm);
        }

        UpdateClockAndEyes(content);
    }

    private void UpdateClockAndEyes(CollapsedContent content)
    {
        bool collapsed = !_isHidden && !_isTucked && _mode == IslandMode.Collapsed;
        bool clockNeeded = !_isHidden && ((collapsed && content.Centre == CentreContent.Clock) || _mode == IslandMode.Expanded);
        _clock.SetRunning(clockNeeded);
        if (clockNeeded)
        {
            _collapsed.SetClock(_clock.Text);
            _expanded?.SetClock(_clock.Text);
        }

        bool eyesShown = collapsed && content.Centre == CentreContent.Eyes && _tuckLook != TuckLook.MiniWave;
        var eyes = _collapsed.Eyes;
        eyes.Mood = eyesShown ? _services.Mascot.Mood : MascotMood.Hidden;
        if (eyesShown)
        {
            eyes.LookX = _services.Mascot.LookX;
            eyes.LookY = _services.Mascot.LookY;
        }
    }

    private void Expand(IslandTab? requested)
    {
        if (_isHidden)
        {
            return;
        }

        _hoverTimer.Stop();
        _leaveTimer.Stop();
        _hoverPending = false;
        var view = EnsureExpanded();
        view.RebuildTabs();
        var tab = ChooseTab(view, requested);
        if (_isExpanded)
        {
            view.Select(tab, Animations);
            return;
        }

        ParkPeek();
        _isExpanded = true;
        view.Open(tab, Animations && _introDone);
        Refresh();
    }

    private void Collapse()
    {
        _leaveTimer.Stop();
        _dragInside = false;
        if (!_isExpanded)
        {
            return;
        }

        _isExpanded = false;
        // Panels, album art and tooltips allocated while open are garbage now; return it once things are quiet.
        MemoryTrimmer.Request(TimeSpan.FromSeconds(20));
        if (!ContinuePeeks())
        {
            Refresh();
        }
    }

    private IslandTab ChooseTab(ExpandedView view, IslandTab? requested)
    {
        var tabs = view.Tabs;
        if (requested is { } wanted && tabs.Contains(wanted))
        {
            return wanted;
        }

        if (tabs.Contains(IslandTab.Music) && _services.Media.Current is not null)
        {
            return IslandTab.Music;
        }

        return tabs.Contains(_lastTab) ? _lastTab : tabs[0];
    }

    private ExpandedView EnsureExpanded()
    {
        if (_expanded is not null)
        {
            return _expanded;
        }

        _expanded = new ExpandedView(_services);
        _expanded.CollapseRequested += (_, _) =>
        {
            _suppressHover = true;
            Collapse();
        };
        _expanded.TabSelected += (_, tab) => _lastTab = tab;
        _expanded.SetClock(_clock.Text);
        Surface.Children.Add(_expanded);
        return _expanded;
    }

    private bool IsHiddenWanted() =>
        _services.Settings.Current.HideInFullscreen && _services.Foreground.IsFullscreenAppForeground;

    private void UpdateHidden()
    {
        bool hide = IsHiddenWanted();
        if (hide == _isHidden)
        {
            return;
        }

        _isHidden = hide;
        if (hide)
        {
            _hoverTimer.Stop();
            _leaveTimer.Stop();
            _hoverPending = false;
            ParkPeek();
            _isExpanded = false;
            _expanded?.Close(animate: false);
            _clock.SetRunning(false);
            StopMusicLine();
            Motion.To(Surface, OpacityProperty, 0, TimeSpan.FromMilliseconds(140), Motion.Decelerate, Animations && IsVisible,
                completed: (_, _) =>
                {
                    if (_isHidden)
                    {
                        Hide();
                    }
                });
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        Anchor();
        NotchPlacement.ReassertTopmost(_hwnd);
        Refresh(animate: false);
        Motion.To(Surface, OpacityProperty, 1, TimeSpan.FromMilliseconds(200), Motion.Decelerate, Animations);
        ContinuePeeks();
    }

    // ---- Peeks -------------------------------------------------------------------------------------------------

    private void OnPeekRequested(object? sender, PeekRequest request)
    {
        if (_currentPeek is { } current && current.Kind == request.Kind && current.Title == request.Title &&
            current.Subtitle == request.Subtitle)
        {
            if (_peekTimer.IsEnabled)
            {
                StartPeekTimer(PeekDuration(request));
            }

            return;
        }

        if (_currentPeek is null && !_isExpanded && !_isHidden)
        {
            ShowPeek(request, PeekDuration(request));
        }
        else
        {
            _peekQueue.Enqueue(request);
        }
    }

    private void ShowPeek(PeekRequest request, TimeSpan duration)
    {
        bool animate = Animations && _introDone;
        var (baseWidth, baseHeight) = _services.Settings.Current.GetNotchSize();
        var outgoing = _peekViews[_peekFront];
        bool replacing = _currentPeek is not null && _mode == IslandMode.Peek;
        if (replacing || outgoing.Visibility == Visibility.Visible)
        {
            _peekFront ^= 1;
        }

        var incoming = _peekViews[_peekFront];
        _currentPeek = request;
        _peekSize = incoming.Prepare(request, baseWidth, baseHeight);
        if (!ReferenceEquals(outgoing, incoming))
        {
            outgoing.Leave(animate);
        }

        incoming.Enter(animate);
        StartPeekTimer(duration);
        Refresh();
    }

    private void EndPeek()
    {
        _peekTimer.Stop();
        _currentPeek = null;
        if (!ContinuePeeks())
        {
            Refresh();
        }
    }

    /// <summary>Shows the next queued peek unless something outranks it. Returns false when nothing was shown.</summary>
    private bool ContinuePeeks()
    {
        if (_isExpanded || _isHidden || _currentPeek is not null || _peekQueue.Dequeue() is not { } next)
        {
            return false;
        }

        ShowPeek(next, PeekDuration(next));
        return true;
    }

    /// <summary>An interrupted peek goes back to the front of the queue (and expires there if it waits too long).</summary>
    private void ParkPeek()
    {
        _peekTimer.Stop();
        if (_currentPeek is { } current)
        {
            _peekQueue.Requeue(current);
            _currentPeek = null;
        }
    }

    private void StartPeekTimer(TimeSpan duration)
    {
        _peekTimer.Stop();
        _peekTimer.Interval = duration;
        _peekTimer.Start();
    }

    private static TimeSpan PeekDuration(PeekRequest request)
    {
        var duration = request.Duration ?? DefaultPeekDuration;
        return duration < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
            : duration > TimeSpan.FromSeconds(20) ? TimeSpan.FromSeconds(20) : duration;
    }

    // ---- Mouse & drag ------------------------------------------------------------------------------------------

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        _leaveTimer.Stop();
        if (_isExpanded || _suppressHover || _isHidden || _revealing)
        {
            return;
        }

        if (_tuckLook != TuckLook.None && _services.Settings.Current.TuckClickToOpen)
        {
            // Tucked: hovering shows the mini notch and leaves it there; a click opens the full panel.
            _revealing = true;
            Refresh();
            return;
        }

        _hoverTimer.Interval = _tuckLook != TuckLook.None ? TuckedHoverDelay : HoverDelay;
        _hoverPending = true;
        _hoverTimer.Stop();
        _hoverTimer.Start();
        Refresh();
    }

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        _suppressHover = false;
        if (_revealing)
        {
            _revealing = false;
            if (!_isExpanded)
            {
                Refresh();
            }
        }

        if (_hoverPending)
        {
            _hoverPending = false;
            _hoverTimer.Stop();
            Refresh();
        }

        if (_isExpanded)
        {
            _leaveTimer.Stop();
            _leaveTimer.Start();
        }
    }

    private void OnHoverElapsed(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        _hoverPending = false;
        if (Surface.IsMouseOver && !_isExpanded)
        {
            Expand(null);
        }
        else
        {
            Refresh();
        }
    }

    private void OnLeaveElapsed(object? sender, EventArgs e)
    {
        _leaveTimer.Stop();
        if (!_isExpanded || _dragInside)
        {
            return;
        }

        bool captured = Mouse.Captured is Visual visual && visual.IsDescendantOf(this);
        if (captured || NotchPlacement.IsCursorOver(Surface))
        {
            // Still on the island (tooltip, slider drag, or a stale hover after drag-and-drop): look again shortly.
            _leaveTimer.Start();
            return;
        }

        Collapse();
    }

    private void OnIslandClick(object sender, MouseButtonEventArgs e)
    {
        if (_isExpanded || e.Handled)
        {
            return;
        }

        e.Handled = true;
        Expand(null);
    }

    private void OnIslandDragOver(object sender, DragEventArgs e)
    {
        if (!_dragInside)
        {
            // First event of this pass over the island: files that are already on the shelf (dragging one out) don't count.
            _dragInside = true;
            _dragHasNewFiles = HasNewFiles(e.Data);
            if (_dragHasNewFiles && !_isHidden)
            {
                _suppressHover = false;
                Expand(IslandTab.Shelf);
                _expanded?.SetDropHint(true, Animations);
            }
        }

        _leaveTimer.Stop();
        if (!e.Handled)
        {
            e.Effects = !_dragHasNewFiles ? DragDropEffects.None
                : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
                : e.AllowedEffects & (DragDropEffects.Link | DragDropEffects.Move);
            e.Handled = true;
        }
    }

    private bool HasNewFiles(IDataObject data)
    {
        try
        {
            if (data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            {
                return false;
            }

            var shelved = new HashSet<string>(_services.Shelf.Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            return paths.Any(p => !shelved.Contains(p));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read dragged data: {ex.Message}");
            return false;
        }
    }

    private void OnIslandDragLeave(object sender, DragEventArgs e)
    {
        _dragInside = false;
        _leaveTimer.Stop();
        _leaveTimer.Start();
        Dispatcher.InvokeAsync(() =>
        {
            if (!_dragInside)
            {
                _expanded?.SetDropHint(false, Animations);
            }
        }, DispatcherPriority.Background);
    }

    private void OnIslandDrop(object sender, DragEventArgs e)
    {
        _dragInside = false;
        _expanded?.SetDropHint(false, Animations);
        if (!e.Handled && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            try
            {
                _services.Shelf.Add(paths);
            }
            catch (Exception ex)
            {
                Log.Error("Shelf rejected dropped files", ex);
            }

            e.Effects = e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.Link;
            e.Handled = true;
        }

        _lastTab = IslandTab.Shelf;
        _expanded?.Select(IslandTab.Shelf, Animations);
        // WPF does not see the cursor after an OLE drop until it moves; let the leave check decide.
        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    // ---- Service & system events -------------------------------------------------------------------------------

    private void OnContentChanged(object? sender, EventArgs e)
    {
        if (!_isHidden)
        {
            Refresh();
        }
    }

    private void OnMascotLook(object? sender, EventArgs e)
    {
        if (_collapsed.ShowsEyes && _mode == IslandMode.Collapsed)
        {
            _collapsed.Eyes.LookX = _services.Mascot.LookX;
            _collapsed.Eyes.LookY = _services.Mascot.LookY;
        }
    }

    private void OnMascotWoke(object? sender, EventArgs e)
    {
        if (_collapsed.ShowsEyes && _mode == IslandMode.Collapsed && !_isHidden)
        {
            _collapsed.Eyes.PlayWake();
        }
    }

    private void OnClockChanged(object? sender, EventArgs e)
    {
        _collapsed.SetClock(_clock.Text);
        _expanded?.SetClock(_clock.Text);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Anchor();
        _expanded?.RebuildTabs();
        UpdateHidden();
        if (!_isHidden)
        {
            Refresh();
        }
    }

    private void OnForegroundChanged(object? sender, EventArgs e)
    {
        NotchPlacement.ReassertTopmost(_hwnd);
        UpdateHidden();
        if (!_isHidden && _tuckLook != ComputeTuckLook(_mode, CollapsedContent.From(_services)))
        {
            Refresh();
        }
    }

    /// <summary>How the collapsed island looks over a window whose top sits under the notch (see <see cref="TuckLook"/>).</summary>
    private TuckLook ComputeTuckLook(IslandMode mode, CollapsedContent content)
    {
        var settings = _services.Settings.Current;
        if (mode != IslandMode.Collapsed || _isHidden || !IsTuckWanted()
            || (content.Centre == CentreContent.Lyric && settings.TuckKeepsLyrics))
        {
            return TuckLook.None;
        }

        // Under the cursor it is the regular mini notch (it opens on click or after a hover, per settings).
        if (_revealing || _hoverPending || Surface.IsMouseOver)
        {
            return TuckLook.None;
        }

        if (content.Left != LeftWing.Art)
        {
            return TuckLook.Line;
        }

        return settings.TuckMusic switch
        {
            TuckMusicView.Wave => TuckLook.Wave,
            TuckMusicView.Mini => TuckLook.Mini,
            TuckMusicView.MiniWave => TuckLook.MiniWave,
            _ => TuckLook.Line,
        };
    }

    /// <summary>The beat wave: alone in the tucked pill, or in the mini notch's centre between the wings.</summary>
    private void UpdateMusicLine(CollapsedContent content, double baseHeight)
    {
        bool wanted = !_isHidden && (_tuckLook is TuckLook.Wave or TuckLook.MiniWave) && content.Left == LeftWing.Art;
        if (!wanted)
        {
            StopMusicLine();
            return;
        }

        // In the mini notch the wave sits between the wings, which are as wide as the notch is tall.
        bool inMini = _tuckLook == TuckLook.MiniWave;
        double inset = inMini ? baseHeight + 2 : 0;
        _wave.Margin = new Thickness(inset, 0, inset, 0);
        _wave.MaxAmplitude = inMini ? 7 : double.PositiveInfinity;

        if (!ReferenceEquals(_musicLineArt, content.Art) || _wave.Visibility != Visibility.Visible)
        {
            _musicLineArt = content.Art;
            _wave.SetColor(ArtColor.Dominant(content.Art) ?? Color.FromRgb(0xF2, 0xF2, 0xF2));
        }

        _wave.Visibility = Visibility.Visible;
        if (Animations)
        {
            _wave.Start();
        }
    }

    private void StopMusicLine()
    {
        _wave.Stop();
        _wave.Visibility = Visibility.Collapsed;
        _musicLineArt = null;
    }

    private bool IsTuckWanted()
    {
        var foreground = _services.Foreground;
        return _services.Settings.Current.Tuck switch
        {
            TuckMode.Browser => foreground.IsBrowserForeground && foreground.IsTopEdgeForeground,
            TuckMode.AnyWindow => foreground.IsTopEdgeForeground,
            _ => false,
        };
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(Anchor, DispatcherPriority.Loaded);

    private void OnSystemTimeChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (_clock.IsRunning)
        {
            _clock.SetRunning(false);
            _clock.SetRunning(true);
        }
    });

    private void Anchor()
    {
        try
        {
            NotchPlacement.Anchor(this, _hwnd, _services.Settings.Current.Position);
        }
        catch (Exception ex)
        {
            Log.Error("Could not anchor the notch window", ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NotchPlacement.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(NotchPlacement.MA_NOACTIVATE);
            case NotchPlacement.WM_DISPLAYCHANGE:
                Dispatcher.InvokeAsync(Anchor, DispatcherPriority.Loaded);
                break;
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Anchor();
        // Let the first frame render the sliver, then drop the island in.
        Dispatcher.InvokeAsync(() =>
        {
            _introDone = true;
            if (_isHidden)
            {
                Hide();
                return;
            }

            if (!ContinuePeeks())
            {
                Refresh();
            }
        }, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _wave.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        _services.Settings.Changed -= OnSettingsChanged;
        _services.Peeks.Requested -= OnPeekRequested;
        _services.Foreground.Changed -= OnForegroundChanged;
        _services.Mascot.Changed -= OnContentChanged;
        _services.Mascot.LookChanged -= OnMascotLook;
        _services.Mascot.Woke -= OnMascotWoke;
        _services.Media.TrackChanged -= OnContentChanged;
        _services.Media.PlaybackChanged -= OnContentChanged;
        _services.Lyrics.LyricsChanged -= OnContentChanged;
        _services.Lyrics.LineChanged -= OnContentChanged;
        _services.Power.Changed -= OnContentChanged;
        _hoverTimer.Stop();
        _leaveTimer.Stop();
        _peekTimer.Stop();
        _clock.SetRunning(false);
    }
}
