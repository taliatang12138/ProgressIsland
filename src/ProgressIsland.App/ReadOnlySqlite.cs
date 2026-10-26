using System.Runtime.InteropServices;

namespace ProgressIsland.App;

// Windows' SQLite runtime, opened READONLY. No NuGet, Python, credential or Codex RPC dependency.
internal sealed class ReadOnlySqlite : IDisposable
{
    private IntPtr db;
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    public ReadOnlySqlite(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Codex 状态数据库不存在", path);
        var code = sqlite3_open_v2(path, out db, 1, IntPtr.Zero); // SQLITE_OPEN_READONLY
        if (code != 0) { Dispose(); throw new IOException("Codex 状态数据库暂不可读：" + code); }
        sqlite3_busy_timeout(db, 100);
    }
    public List<Dictionary<string, string?>> Query(string sql)
    {
        var code = sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero);
        if (code != 0) throw new IOException("Codex 状态结构暂不兼容：" + code);
        try
        {
            var rows = new List<Dictionary<string, string?>>();
            while ((code = sqlite3_step(statement)) == 100)
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < sqlite3_column_count(statement); c++)
                    row[Marshal.PtrToStringUTF8(sqlite3_column_name(statement, c))!] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, c));
                rows.Add(row);
            }
            if (code != 101) throw new IOException("Codex 状态读取暂不可用：" + code);
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }
    public bool HasColumn(string table, string column) => Query("PRAGMA table_info(" + table + ")").Any(row => row.GetValueOrDefault("name") == column);
    public void Dispose() { if (db != IntPtr.Zero) { sqlite3_close_v2(db); db = IntPtr.Zero; } }
}
