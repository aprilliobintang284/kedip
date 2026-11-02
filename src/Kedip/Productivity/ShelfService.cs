using System.Collections.ObjectModel;
using System.Windows.Media;
using Kedip.Core;

namespace Kedip.Productivity;

public sealed class ShelfItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public DateTime AddedAt { get; init; } = DateTime.Now;

    /// <summary>Frozen shell icon, or null.</summary>
    public ImageSource? Icon { get; set; }
}

/// <summary>
/// Temporary shelf of file references (not copies), persisted in shelf.json.
/// OWNER: E. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class ShelfService : IAppService
{
    /// <summary>Mutated on the UI thread only.</summary>
    public ObservableCollection<ShelfItem> Items { get; } = [];

    public void Start()
    {
    }

    /// <summary>Adds existing files/folders, skipping duplicates.</summary>
    public void Add(IEnumerable<string> paths)
    {
    }

    public void Remove(ShelfItem item)
    {
    }

    public void Clear()
    {
    }

    public void Dispose()
    {
    }
}
