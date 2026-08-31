namespace MmProtect.InstanceAgent.Infrastructure.Persistence;

internal static class SqliteProviderInitializer
{
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        }
    }
}
