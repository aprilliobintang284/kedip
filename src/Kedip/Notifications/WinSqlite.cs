using System.Runtime.InteropServices;
using System.Text;

namespace Kedip.Notifications;

/// <summary>
/// Minimal read-only access to SQLite through Windows' own winsqlite3.dll (System32), so no extra library ships.
/// </summary>
internal sealed class WinSqlite : IDisposable
{
    private const string Dll = "winsqlite3.dll";
    private const int SQLITE_ROW = 100;
    private const int SQLITE_OPEN_READONLY = 0x00000001;
    private const int SQLITE_OPEN_URI = 0x00000040;
    private const int SQLITE_OPEN_NOMUTEX = 0x00008000;

    private IntPtr _db;

    private WinSqlite(IntPtr db) => _db = db;

    /// <summary>Opens <paramref name="path"/> read-only (works while another process writes it in WAL mode).</summary>
    public static WinSqlite? OpenReadOnly(string path)
    {
        string uri = "file:///" + path.Replace('\\', '/').Replace("?", "%3f").Replace("#", "%23") + "?mode=ro";
        byte[] utf8 = Encoding.UTF8.GetBytes(uri + "\0");
        int rc = sqlite3_open_v2(utf8, out IntPtr db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI | SQLITE_OPEN_NOMUTEX, IntPtr.Zero);
        if (rc != 0)
        {
            if (db != IntPtr.Zero)
            {
                sqlite3_close_v2(db);
            }

            return null;
        }

        sqlite3_busy_timeout(db, 250);
        return new WinSqlite(db);
    }

    /// <summary>Runs <paramref name="sql"/> with optional int64 parameters (?1, ?2, …) and calls <paramref name="row"/> per result row.</summary>
    public bool Query(string sql, Action<Row> row, params long[] args)
    {
        if (_db == IntPtr.Zero || sqlite3_prepare16_v2(_db, sql, -1, out IntPtr stmt, IntPtr.Zero) != 0)
        {
            return false;
        }

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                sqlite3_bind_int64(stmt, i + 1, args[i]);
            }

            var current = new Row(stmt);
            while (sqlite3_step(stmt) == SQLITE_ROW)
            {
                row(current);
            }

            return true;
        }
        finally
        {
            sqlite3_finalize(stmt);
        }
    }

    /// <summary>First column of the first row, or null.</summary>
    public long? ScalarInt64(string sql)
    {
        long? value = null;
        return Query(sql, r => value ??= r.Int64(0)) ? value : null;
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero)
        {
            sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
    }

    internal readonly struct Row(IntPtr stmt)
    {
        public long Int64(int column) => sqlite3_column_int64(stmt, column);

        public string Text(int column) => Marshal.PtrToStringUni(sqlite3_column_text16(stmt, column)) ?? "";

        public byte[] Blob(int column)
        {
            IntPtr data = sqlite3_column_blob(stmt, column);
            int length = sqlite3_column_bytes(stmt, column);
            if (data == IntPtr.Zero || length <= 0)
            {
                return [];
            }

            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
            return bytes;
        }
    }

    [DllImport(Dll)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Dll)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport(Dll)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    private static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int nBytes, out IntPtr stmt, IntPtr tail);

    [DllImport(Dll)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Dll)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport(Dll)]
    private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

    [DllImport(Dll)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int column);

    [DllImport(Dll)]
    private static extern IntPtr sqlite3_column_text16(IntPtr stmt, int column);

    [DllImport(Dll)]
    private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);

    [DllImport(Dll)]
    private static extern int sqlite3_column_bytes(IntPtr stmt, int column);
}
