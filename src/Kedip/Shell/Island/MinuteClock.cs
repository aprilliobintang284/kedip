using System.Globalization;
using System.Windows.Threading;

namespace Kedip.Shell.Island;

/// <summary>"HH:mm" clock that wakes exactly once per minute (aligned to the minute boundary) and only while running.</summary>
internal sealed class MinuteClock
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private string _text = Format(DateTime.Now);

    public MinuteClock()
    {
        _timer.Tick += OnTick;
    }

    /// <summary>Raised on the UI thread when <see cref="Text"/> changes.</summary>
    public event EventHandler? Changed;

    public string Text => _text;

    public bool IsRunning => _timer.IsEnabled;

    public void SetRunning(bool running)
    {
        if (running == IsRunning)
        {
            return;
        }

        if (running)
        {
            Refresh();
            Schedule();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>Re-reads the time now (e.g. after resume or a time-zone change).</summary>
    public void Refresh()
    {
        string text = Format(DateTime.Now);
        if (text != _text)
        {
            _text = text;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        Refresh();
        Schedule();
    }

    private void Schedule()
    {
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
        _timer.Interval = next - now + TimeSpan.FromMilliseconds(40);
        _timer.Start();
    }

    private static string Format(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
