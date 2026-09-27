namespace MicBoost.Core;

/// <summary>
/// Tiny size-capped file logger (%AppData%\MicBoost\micboost.log). Used mostly to explain
/// why a boost method is unavailable on a given machine.
/// </summary>
public static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(AppPaths.DataDirectory, "micboost.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    // Keep one previous generation.
                    File.Copy(FilePath, FilePath + ".old", overwrite: true);
                    File.Delete(FilePath);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicBoost");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");
}
