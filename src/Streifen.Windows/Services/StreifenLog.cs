namespace Streifen.Windows.Services;

/// <summary>
/// Simple file logger to %APPDATA%\Streifen\streifen.log
/// </summary>
public static class StreifenLog
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Streifen");
    private static readonly string LogFile = Path.Combine(LogDir, "streifen.log");
    private static readonly object Lock = new();

    static StreifenLog()
    {
        Directory.CreateDirectory(LogDir);

        // Truncate on startup if over 1MB
        if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 1_000_000)
        {
            try { File.Delete(LogFile); } catch { }
        }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (Lock)
        {
            try
            {
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch
            {
                // Don't crash on log failure
            }
        }
    }
}
