using Kedip.Core;

namespace Kedip.Shell.Island;

/// <summary>Pending peeks: highest <see cref="PeekRequest.Priority"/> first, then first-in first-out. Stale entries are dropped.</summary>
internal sealed class PeekQueue
{
    private const int Capacity = 12;
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(45);

    private readonly List<Entry> _items = [];
    private long _sequence;
    private long _requeued;

    public int Count => _items.Count;

    public void Enqueue(PeekRequest request)
    {
        // A newer message of the same kind and title replaces the queued one (e.g. plug, unplug, plug).
        int existing = _items.FindIndex(e => e.Request.Kind == request.Kind && e.Request.Title == request.Title);
        if (existing >= 0)
        {
            _items[existing] = _items[existing] with { Request = request, Queued = Environment.TickCount64 };
            return;
        }

        _items.Add(new Entry(request, _sequence++, Environment.TickCount64));
        if (_items.Count > Capacity)
        {
            _items.Remove(_items.OrderBy(e => e.Request.Priority).ThenBy(e => e.Sequence).First());
        }
    }

    /// <summary>Puts an interrupted peek back at the front of its priority band.</summary>
    public void Requeue(PeekRequest request)
    {
        _items.RemoveAll(e => e.Request.Kind == request.Kind && e.Request.Title == request.Title);
        _items.Add(new Entry(request, -(++_requeued), Environment.TickCount64));
    }

    /// <summary>The next fresh request, or null.</summary>
    public PeekRequest? Dequeue()
    {
        long now = Environment.TickCount64;
        _items.RemoveAll(e => now - e.Queued > MaxAge.TotalMilliseconds);
        if (_items.Count == 0)
        {
            return null;
        }

        var best = _items[0];
        foreach (var entry in _items)
        {
            if (entry.Request.Priority > best.Request.Priority ||
                (entry.Request.Priority == best.Request.Priority && entry.Sequence < best.Sequence))
            {
                best = entry;
            }
        }

        _items.Remove(best);
        return best.Request;
    }

    public void Clear() => _items.Clear();

    private readonly record struct Entry(PeekRequest Request, long Sequence, long Queued);
}
