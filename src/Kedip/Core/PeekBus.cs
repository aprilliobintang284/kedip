namespace Kedip.Core;

public enum PeekKind { Info, Charging, Battery, Music, Timer, Water, Prayer, Shelf }

/// <summary>
/// A short message the notch shows for a few seconds.
/// <paramref name="Glyph"/> is a character from the IconFont (Segoe Fluent Icons);
/// <paramref name="AccentHex"/> colours the glyph (e.g. "#34C759").
/// </summary>
public sealed record PeekRequest(
    PeekKind Kind,
    string Title,
    string? Subtitle = null,
    string? Glyph = null,
    TimeSpan? Duration = null,
    int Priority = 0,
    string? AccentHex = null);

/// <summary>Services post peeks here; the notch shell displays them (queued by priority).</summary>
public sealed class PeekBus
{
    public event EventHandler<PeekRequest>? Requested;

    /// <summary>Must be called on the UI thread.</summary>
    public void Show(PeekRequest request) => Requested?.Invoke(this, request);
}
