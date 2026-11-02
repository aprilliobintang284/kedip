using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kedip.Core;

namespace Kedip.Productivity;

public sealed class ShelfItem : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public required string Path { get; init; }
    public required string Name { get; init; }
    public DateTime AddedAt { get; init; } = DateTime.Now;

    /// <summary>True for directories (used for the placeholder glyph until the shell icon loads).</summary>
    public bool IsFolder { get; init; }

    /// <summary>Frozen shell icon, or null. Loaded lazily; raises <see cref="PropertyChanged"/> when set.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (!ReferenceEquals(_icon, value))
            {
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Set once the icon was queued for loading (guarded by the service's icon lock).</summary>
    internal bool IconQueued { get; set; }
}

/// <summary>
/// Temporary shelf of file references (not copies), persisted in shelf.json; newest first, at most 30.
/// File-system probing runs on the thread pool; shell icons are fetched only after the UI asks for them
/// (<see cref="RequestIcons"/>), on a short-lived STA thread, and converted/frozen on the UI thread.
/// </summary>
public sealed class ShelfService : IAppService
{
    public const int MaxItems = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly BackgroundFileWriter _writer = new(AppPaths.ShelfFile);
    private readonly Lock _iconGate = new();
    private readonly Queue<ShelfItem> _iconQueue = new();
    private bool _iconWorkerRunning;
    private bool _iconsWanted;
    private bool _started;
    private bool _disposed;

    /// <summary>Mutated on the UI thread only.</summary>
    public ObservableCollection<ShelfItem> Items { get; } = [];

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Task.Run(LoadAndProbe).ContinueWith(
            t => _dispatcher.InvokeAsync(() => ApplyLoaded(t.Result)), TaskScheduler.Default);
    }

    /// <summary>Adds existing files/folders, skipping duplicates.</summary>
    /// <remarks>Existence is checked off the UI thread, so items appear a moment later.</remarks>
    public void Add(IEnumerable<string> paths)
    {
        var candidates = paths
            .Select(NormalizePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !Contains(p))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        Task.Run(() => Probe(candidates)).ContinueWith(
            t => _dispatcher.InvokeAsync(() => InsertNew(t.Result)), TaskScheduler.Default);
    }

    public void Remove(ShelfItem item)
    {
        if (Items.Remove(item))
        {
            Save();
        }
    }

    public void Clear()
    {
        if (Items.Count == 0)
        {
            return;
        }

        Items.Clear();
        Save();
    }

    public bool Contains(string path) =>
        Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes items whose file or folder no longer exists (checked off the UI thread).</summary>
    public void PruneMissing()
    {
        if (Items.Count == 0)
        {
            return;
        }

        var paths = Items.Select(i => i.Path).ToList();
        Task.Run(() => paths.Where(p => !Exists(p)).ToList()).ContinueWith(
            t => _dispatcher.InvokeAsync(() => RemovePaths(t.Result)), TaskScheduler.Default);
    }

    /// <summary>
    /// Starts loading shell icons for items that have none; afterwards icons of new items load automatically.
    /// Call when the shelf becomes visible, so a session that never opens the shelf never touches the shell.
    /// </summary>
    public void RequestIcons()
    {
        _iconsWanted = true;
        QueueIcons(Items);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_iconGate)
        {
            _iconQueue.Clear();
        }

        _writer.Flush(TimeSpan.FromSeconds(2));
    }

    // Thread pool.
    private static LoadResult LoadAndProbe()
    {
        try
        {
            if (!File.Exists(AppPaths.ShelfFile))
            {
                return new LoadResult([], false);
            }

            var data = JsonSerializer.Deserialize<ShelfFileData>(File.ReadAllText(AppPaths.ShelfFile), JsonOptions);
            var stored = data?.Items ?? [];
            var found = new List<ShelfEntry>(stored.Count);
            foreach (var entry in stored)
            {
                string? path = NormalizePath(entry.Path);
                if (path is null || found.Exists(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (TryProbe(path, out bool isFolder))
                {
                    found.Add(new ShelfEntry(path, entry.AddedAt == default ? DateTime.Now : entry.AddedAt, isFolder));
                }
            }

            bool pruned = found.Count != stored.Count;
            if (pruned)
            {
                Log.Info($"Shelf: pruned {stored.Count - found.Count} missing item(s)");
            }

            return new LoadResult(found, pruned);
        }
        catch (Exception ex)
        {
            Log.Error("shelf.json is unreadable; starting with an empty shelf", ex);
            return new LoadResult([], false);
        }
    }

    // Thread pool.
    private static List<ShelfEntry> Probe(List<string> paths)
    {
        var found = new List<ShelfEntry>(paths.Count);
        foreach (string path in paths)
        {
            if (TryProbe(path, out bool isFolder))
            {
                found.Add(new ShelfEntry(path, DateTime.Now, isFolder));
            }
        }

        return found;
    }

    private void ApplyLoaded(LoadResult loaded)
    {
        if (_disposed)
        {
            return;
        }

        // Items dropped while loading are newer and stay in front.
        bool hadItems = Items.Count > 0;
        var added = new List<ShelfItem>();
        foreach (var entry in loaded.Entries)
        {
            if (Items.Count >= MaxItems)
            {
                break;
            }

            if (!Contains(entry.Path))
            {
                var item = CreateItem(entry);
                Items.Add(item);
                added.Add(item);
            }
        }

        // Rewrite only when pruning/merging changed what is on disk.
        if (loaded.Pruned || hadItems || added.Count != loaded.Entries.Count)
        {
            Save();
        }

        if (_iconsWanted)
        {
            QueueIcons(added);
        }
    }

    private void InsertNew(List<ShelfEntry> entries)
    {
        if (_disposed)
        {
            return;
        }

        var added = new List<ShelfItem>();
        int index = 0;
        foreach (var entry in entries)
        {
            if (Contains(entry.Path))
            {
                continue;
            }

            var item = CreateItem(entry);
            Items.Insert(index++, item);
            added.Add(item);
        }

        if (added.Count == 0)
        {
            return;
        }

        while (Items.Count > MaxItems)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        Save();
        if (_iconsWanted)
        {
            QueueIcons(added.Where(Items.Contains));
        }
    }

    private void RemovePaths(List<string> missing)
    {
        if (_disposed || missing.Count == 0)
        {
            return;
        }

        var set = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
        bool removed = false;
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (set.Contains(Items[i].Path))
            {
                Items.RemoveAt(i);
                removed = true;
            }
        }

        if (removed)
        {
            Save();
        }
    }

    private void Save()
    {
        var data = new ShelfFileData
        {
            Items = Items.Select(i => new ShelfFileEntry { Path = i.Path, AddedAt = i.AddedAt }).ToList(),
        };
        _writer.Write(JsonSerializer.Serialize(data, JsonOptions));
    }

    private static ShelfItem CreateItem(ShelfEntry entry)
    {
        string name = System.IO.Path.GetFileName(entry.Path);
        return new ShelfItem
        {
            Path = entry.Path,
            Name = string.IsNullOrEmpty(name) ? entry.Path : name,
            AddedAt = entry.AddedAt,
            IsFolder = entry.IsFolder,
        };
    }

    private void QueueIcons(IEnumerable<ShelfItem> items)
    {
        lock (_iconGate)
        {
            foreach (var item in items)
            {
                if (item.Icon is null && !item.IconQueued)
                {
                    item.IconQueued = true;
                    _iconQueue.Enqueue(item);
                }
            }

            if (_iconWorkerRunning || _iconQueue.Count == 0 || _disposed)
            {
                return;
            }

            _iconWorkerRunning = true;
        }

        try
        {
            // Shell icon handlers expect COM; a short-lived STA thread is the most compatible host.
            var thread = new Thread(IconWorker) { IsBackground = true, Name = "Kedip shelf icons" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the shelf icon thread", ex);
            lock (_iconGate)
            {
                _iconWorkerRunning = false;
            }
        }
    }

    // Icon thread: only the HICON is fetched here; the bitmap is created on the UI thread.
    private void IconWorker()
    {
        while (true)
        {
            ShelfItem item;
            lock (_iconGate)
            {
                if (_iconQueue.Count == 0 || _disposed)
                {
                    _iconWorkerRunning = false;
                    return;
                }

                item = _iconQueue.Dequeue();
            }

            IntPtr icon = ShellNative.GetLargeIcon(item.Path);
            if (icon != IntPtr.Zero)
            {
                _dispatcher.InvokeAsync(() => ApplyIcon(item, icon), DispatcherPriority.Background);
            }
        }
    }

    private static void ApplyIcon(ShelfItem item, IntPtr icon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            item.Icon = source;
        }
        catch (Exception ex)
        {
            Log.Warn($"Shelf icon failed for {item.Name}: {ex.Message}");
        }
        finally
        {
            ShellNative.DestroyIcon(icon);
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string full = System.IO.Path.GetFullPath(path.Trim());
            string root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryProbe(string path, out bool isFolder)
    {
        isFolder = false;
        try
        {
            if (File.Exists(path))
            {
                return true;
            }

            if (Directory.Exists(path))
            {
                isFolder = true;
                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static bool Exists(string path) => TryProbe(path, out _);

    private readonly record struct ShelfEntry(string Path, DateTime AddedAt, bool IsFolder);

    private sealed record LoadResult(List<ShelfEntry> Entries, bool Pruned);

    private sealed class ShelfFileData
    {
        public List<ShelfFileEntry> Items { get; set; } = [];
    }

    private sealed class ShelfFileEntry
    {
        public string Path { get; set; } = "";
        public DateTime AddedAt { get; set; }
    }

    private static class ShellNative
    {
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>The shell's large icon for <paramref name="path"/> (caller destroys it), or zero.</summary>
        public static IntPtr GetLargeIcon(string path)
        {
            try
            {
                var info = new SHFILEINFO();
                IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
                return result != IntPtr.Zero ? info.hIcon : IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Log.Warn($"SHGetFileInfo failed: {ex.Message}");
                return IntPtr.Zero;
            }
        }
    }
}
