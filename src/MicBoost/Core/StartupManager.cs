using Microsoft.Win32;

namespace MicBoost.Core;

/// <summary>"Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin needed).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MicBoost";

    private static string Command => $"\"{Environment.ProcessPath}\" --startup";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value &&
                       value.Contains(Path.GetFileName(Environment.ProcessPath ?? "MicBoost.exe"),
                                      StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not update Run key: {ex.Message}");
            throw;
        }
    }

    /// <summary>If the exe was moved since autostart was enabled, point the Run entry at the new path.</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string value && !string.Equals(value, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        catch
        {
            // Not critical.
        }
    }
}
