using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using MicBoost.Core;
using Microsoft.Win32;

namespace MicBoost.Audio;

public enum ApoStatus
{
    /// <summary>Equalizer APO is not installed.</summary>
    NotInstalled,
    /// <summary>Installed, but not enabled for this microphone in the Configurator.</summary>
    NotEnabledForDevice,
    /// <summary>Enabled for the mic, but MicBoost's include file is not set up yet (needs admin once).</summary>
    SetupRequired,
    /// <summary>Ready: MicBoost can write the preamp without elevation.</summary>
    Ready,
}

/// <summary>
/// Software gain through Equalizer APO (https://sourceforge.net/projects/equalizerapo/).
///
/// Equalizer APO is a system-wide audio processing object that reads
/// "&lt;config dir&gt;\config.txt" and reloads it automatically when it changes. MicBoost adds one
/// line, "Include: MicBoost.txt", to config.txt, and writes its own file:
///
///     Device: {endpoint-guid}
///     Preamp: +6.0 dB
///     Device: all
///
/// The config folder lives under Program Files, so the first setup has to run elevated.
/// That one-time step also grants the current user write access to MicBoost.txt, so every
/// later level change is written without any UAC prompt.
/// </summary>
public sealed class EqualizerApo
{
    public const string IncludeFileName = "MicBoost.txt";
    public const string DownloadUrl = "https://sourceforge.net/projects/equalizerapo/";
    public const string SetupArgument = "--apo-setup";

    private const string IncludeLine = "Include: " + IncludeFileName;
    private readonly Dictionary<string, bool> _enabledCache = new(StringComparer.OrdinalIgnoreCase);
    private bool? _setupDone;

    public string? ConfigDirectory { get; private set; }
    public string? InstallDirectory { get; private set; }

    public bool IsInstalled => ConfigDirectory != null;
    public string? ConfiguratorPath =>
        InstallDirectory == null ? null : Path.Combine(InstallDirectory, "Configurator.exe");

    public EqualizerApo() => Refresh();

    /// <summary>Re-detects the installation (e.g. after the user installed APO while we ran).</summary>
    public void Refresh()
    {
        _enabledCache.Clear();
        _setupDone = null;
        (InstallDirectory, ConfigDirectory) = FindInstallation();
    }

    public ApoStatus GetStatus(string deviceId)
    {
        if (!IsInstalled) return ApoStatus.NotInstalled;
        if (!IsEnabledForDevice(deviceId)) return ApoStatus.NotEnabledForDevice;
        return IsSetupDone() ? ApoStatus.Ready : ApoStatus.SetupRequired;
    }

    /// <summary>
    /// Writes MicBoost.txt with one preamp block per device. Devices with 0 dB are omitted.
    /// Returns false if the file is not writable (setup missing or permissions reset).
    /// </summary>
    public bool WritePreamps(IReadOnlyDictionary<string, (string Name, double Db)> devices)
    {
        if (ConfigDirectory == null) return false;

        var sb = new StringBuilder();
        sb.AppendLine("# Written by MicBoost - changes to this file are overwritten.");
        sb.AppendLine("# Included from config.txt; applies a preamp gain to the selected microphone(s).");
        foreach (var (id, (name, db)) in devices)
        {
            if (db <= 0.001) continue;
            var guid = EndpointGuid(id);
            if (guid == null) continue;
            sb.AppendLine();
            sb.AppendLine($"# {name.Replace('\r', ' ').Replace('\n', ' ')}");
            sb.AppendLine($"Device: {guid}");
            sb.AppendLine($"Preamp: {db.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)} dB");
        }
        // Reset the device filter so nothing that follows the Include line in config.txt
        // is accidentally restricted to our microphone.
        sb.AppendLine();
        sb.AppendLine("Device: all");

        var path = Path.Combine(ConfigDirectory, IncludeFileName);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // FileMode.Create truncates in place, which keeps the ACL granted during setup.
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(sb.ToString());
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                _setupDone = false;
                return false;
            }
            catch (IOException) when (attempt < 2)
            {
                // Equalizer APO may be reading the file right now.
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                Log.Warn($"Writing {IncludeFileName} failed: {ex.Message}");
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Launches this exe elevated ("runas") to perform the one-time config setup.
    /// Returns true on success; false if the user declined UAC or setup failed.
    /// </summary>
    public bool RunElevatedSetup()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null || Environment.ProcessPath == null) return false;

            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"{SetupArgument} {sid}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process == null) return false;
            process.WaitForExit(30_000);
            _setupDone = null;
            return process.HasExited && process.ExitCode == 0 && IsSetupDone();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user clicked "No" on the UAC prompt.
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Elevated APO setup failed to start: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Entry point of the elevated helper process ("MicBoost.exe --apo-setup &lt;user SID&gt;").
    /// Creates MicBoost.txt, grants the user modify rights on it, and adds the Include line.
    /// </summary>
    public static int PerformSetup(string userSid)
    {
        try
        {
            var sid = new SecurityIdentifier(userSid); // validates the argument
            var (_, configDir) = FindInstallation();
            if (configDir == null) return 2;

            var includePath = Path.Combine(configDir, IncludeFileName);
            if (!File.Exists(includePath))
                File.WriteAllText(includePath, "# Written by MicBoost\r\nPreamp: 0 dB\r\n", new UTF8Encoding(false));

            var file = new FileInfo(includePath);
            var security = file.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, AccessControlType.Allow));
            file.SetAccessControl(security);

            var configPath = Path.Combine(configDir, "config.txt");
            var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
            if (!HasIncludeLine(existing))
            {
                var prefix = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : Environment.NewLine;
                File.AppendAllText(configPath, $"{prefix}{IncludeLine}{Environment.NewLine}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error($"APO setup (elevated) failed: {ex}");
            return 1;
        }
    }

    // ------------------------------------------------------------------------------

    private bool IsSetupDone()
    {
        if (_setupDone.HasValue) return _setupDone.Value;
        _setupDone = false;
        if (ConfigDirectory == null) return false;
        try
        {
            var configPath = Path.Combine(ConfigDirectory, "config.txt");
            var includePath = Path.Combine(ConfigDirectory, IncludeFileName);
            if (!File.Exists(configPath) || !File.Exists(includePath)) return false;
            if (!HasIncludeLine(File.ReadAllText(configPath))) return false;

            // Probe write access without modifying the file.
            using (new FileStream(includePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
            _setupDone = true;
        }
        catch
        {
            _setupDone = false;
        }
        return _setupDone.Value;
    }

    private static bool HasIncludeLine(string config) =>
        Regex.IsMatch(config, @"^\s*Include:\s*" + Regex.Escape(IncludeFileName) + @"\s*$",
                      RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Checks whether Equalizer APO is registered as an effect on this capture endpoint.
    /// The Configurator writes APO CLSIDs into the endpoint's FxProperties key; we resolve
    /// each CLSID found there and look for EqualizerAPO in its InprocServer32 path, which
    /// avoids hard-coding CLSIDs that differ between APO versions.
    /// </summary>
    public bool IsEnabledForDevice(string deviceId)
    {
        if (_enabledCache.TryGetValue(deviceId, out bool cached)) return cached;
        bool enabled = false;
        try
        {
            var guid = EndpointGuid(deviceId);
            if (guid != null)
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var fx = hklm.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{guid}\FxProperties");
                if (fx != null)
                {
                    foreach (var name in fx.GetValueNames())
                    {
                        var values = fx.GetValue(name) switch
                        {
                            string s => new[] { s },
                            string[] arr => arr,
                            _ => Array.Empty<string>(),
                        };
                        foreach (var value in values)
                        foreach (Match m in Regex.Matches(value, @"\{[0-9A-Fa-f\-]{36}\}"))
                        {
                            if (IsEqualizerApoClsid(hklm, m.Value)) { enabled = true; break; }
                        }
                        if (enabled) break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read FxProperties for {deviceId}: {ex.Message}");
        }
        _enabledCache[deviceId] = enabled;
        return enabled;
    }

    private static bool IsEqualizerApoClsid(RegistryKey hklm, string clsid)
    {
        using var server = hklm.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid}\InprocServer32");
        return server?.GetValue(null) is string path &&
               path.Contains("EqualizerAPO", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Endpoint IDs look like "{0.0.1.00000000}.{guid}"; the second part names the registry key.</summary>
    public static string? EndpointGuid(string deviceId)
    {
        int dot = deviceId.LastIndexOf("}.{", StringComparison.Ordinal);
        return dot < 0 ? null : deviceId[(dot + 2)..];
    }

    private static (string? install, string? config) FindInstallation()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            var install = key?.GetValue("InstallPath") as string;
            var config = key?.GetValue("ConfigPath") as string;
            if (config == null && install != null) config = Path.Combine(install, "config");
            if (config != null && Directory.Exists(config)) return (install, config);
        }
        catch
        {
            // fall through to the default location
        }

        var defaultInstall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
        var defaultConfig = Path.Combine(defaultInstall, "config");
        return File.Exists(Path.Combine(defaultConfig, "config.txt")) ? (defaultInstall, defaultConfig) : (null, null);
    }
}
