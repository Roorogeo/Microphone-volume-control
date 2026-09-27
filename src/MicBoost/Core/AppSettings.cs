using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicBoost.Core;

/// <summary>
/// Everything MicBoost remembers, stored in %AppData%\MicBoost\settings.json.
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    // --- Device selection -----------------------------------------------------------
    /// <summary>Follow the Windows default recording device instead of <see cref="SelectedDeviceId"/>.</summary>
    public bool FollowDefaultDevice { get; set; } = true;
    public string? SelectedDeviceId { get; set; }

    // --- Behaviour ------------------------------------------------------------------
    public bool LockVolume { get; set; }
    public bool ShowOsd { get; set; } = true;

    // --- Hotkeys (format: "Ctrl+Alt+PageUp") ---------------------------------------
    public bool HotkeysEnabled { get; set; } = true;
    public string HotkeyVolumeUp { get; set; } = "Ctrl+Alt+PageUp";
    public string HotkeyVolumeDown { get; set; } = "Ctrl+Alt+PageDown";
    public string HotkeyToggleMute { get; set; } = "Ctrl+Alt+M";

    // --- Boost ----------------------------------------------------------------------
    /// <summary>Total boost (dB) reached at 200 %. 100..200 % maps linearly onto 0..MaxBoostDb.</summary>
    public double MaxBoostDb { get; set; } = 30;
    public bool UseHardwareBoost { get; set; } = true;
    public bool UseEqualizerApo { get; set; } = true;
    public bool UseVirtualCable { get; set; }
    /// <summary>Render endpoint of the virtual cable (e.g. "CABLE Input"). Null = auto-detect.</summary>
    public string? VirtualCableDeviceId { get; set; }

    // --- One-time notices -------------------------------------------------------------
    public bool ApoNoticeShown { get; set; }
    public bool ApoNotEnabledNoticeShown { get; set; }

    /// <summary>Per-device level and boost, keyed by Core Audio endpoint ID.</summary>
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DeviceSettings GetDevice(string deviceId)
    {
        if (!Devices.TryGetValue(deviceId, out var device))
        {
            device = new DeviceSettings();
            Devices[deviceId] = device;
        }
        return device;
    }
}

public sealed class DeviceSettings
{
    public string? Name { get; set; }
    /// <summary>Slider position, 0..200 %.</summary>
    public double LevelPercent { get; set; } = 100;
    /// <summary>Boost applied by the hardware Microphone Boost control (dB above neutral).</summary>
    public double HardwareBoostDb { get; set; }
    /// <summary>Boost applied in software (Equalizer APO preamp or virtual-cable gain), dB.</summary>
    public double SoftwareBoostDb { get; set; }
    /// <summary>"EqualizerApo", "VirtualCable" or null.</summary>
    public string? SoftwareBoostMethod { get; set; }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings != null)
                {
                    // Re-create the dictionary so lookups stay case-insensitive.
                    settings.Devices = new Dictionary<string, DeviceSettings>(
                        settings.Devices ?? new(), StringComparer.OrdinalIgnoreCase);
                    settings.MaxBoostDb = Math.Clamp(settings.MaxBoostDb, 3, 40);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"settings.json unreadable, starting with defaults: {ex.Message}");
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var json = JsonSerializer.Serialize(settings, Options);
            // Write to a temp file first so a crash mid-write never corrupts the settings.
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save settings: {ex.Message}");
        }
    }
}
