using System.Diagnostics;
using MicBoost.Audio;
using MicBoost.Core;

namespace MicBoost.UI;

public enum ApoSetupAnswer
{
    Continue,
    NotNow,
    Never,
}

/// <summary>Task dialogs explaining the Equalizer APO requirements (shown at most once each).</summary>
public static class Dialogs
{
    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {target}: {ex.Message}"); }
    }

    /// <summary>Equalizer APO is missing: explain the boost chain and link to the download.</summary>
    public static void ShowApoNotInstalled(bool hasHardwareBoost, float hardwareMaxDb)
    {
        var download = new TaskDialogCommandLinkButton("Download Equalizer APO",
            "Free and open source. During setup, tick your microphone on the \"Capture devices\" tab, then restart Windows.");
        var close = new TaskDialogCommandLinkButton("Not now", "The slider still works up to what your hardware supports.");

        string hw = hasHardwareBoost
            ? $"Your microphone's hardware boost covers the first {hardwareMaxDb:0.#} dB. "
            : "Your microphone driver has no hardware boost control. ";

        var page = new TaskDialogPage
        {
            Caption = "MicBoost",
            Heading = "More boost needs Equalizer APO",
            Text = hw + "To go further, MicBoost adds precise software gain through Equalizer APO, " +
                   "a free system-wide audio processor. MicBoost only writes one \"Preamp\" line to its own " +
                   "config file; nothing else in your audio setup changes.\n\n" +
                   $"<a href=\"{EqualizerApo.DownloadUrl}\">{EqualizerApo.DownloadUrl}</a>",
            EnableLinks = true,
            Icon = TaskDialogIcon.Information,
            Buttons = { download, close },
            Footnote = new TaskDialogFootnote("This message is shown only once. Settings has a download link too."),
        };
        page.LinkClicked += (_, e) => Open(e.LinkHref);

        if (TaskDialog.ShowDialog(page) == download)
            Open(EqualizerApo.DownloadUrl);
    }

    /// <summary>APO is installed but not attached to this microphone.</summary>
    public static void ShowApoNotEnabled(string deviceName, string? configuratorPath)
    {
        var open = new TaskDialogCommandLinkButton("Open the Equalizer APO Configurator",
            $"Tick \"{deviceName}\" on the Capture devices tab, click OK and restart Windows.")
        {
            ShowShieldIcon = true,
            Enabled = configuratorPath != null && File.Exists(configuratorPath),
        };
        var close = new TaskDialogCommandLinkButton("Not now");

        var page = new TaskDialogPage
        {
            Caption = "MicBoost",
            Heading = "Equalizer APO is not enabled for this microphone",
            Text = "Equalizer APO is installed, but it has not been attached to the selected microphone yet, " +
                   "so MicBoost cannot add software gain to it.",
            Icon = TaskDialogIcon.Information,
            Buttons = { open, close },
            Footnote = new TaskDialogFootnote("This message is shown only once."),
        };

        if (TaskDialog.ShowDialog(page) == open && configuratorPath != null)
            Open(configuratorPath);
    }

    /// <summary>Asks for the one-time elevation needed to hook MicBoost into Equalizer APO's config.</summary>
    public static ApoSetupAnswer AskApoSetup()
    {
        var yes = new TaskDialogCommandLinkButton("Continue",
            "Adds \"Include: MicBoost.txt\" to Equalizer APO's config.txt and lets MicBoost update that file " +
            "without further prompts.")
        {
            ShowShieldIcon = true,
        };
        var later = new TaskDialogCommandLinkButton("Not now");
        var never = new TaskDialogCommandLinkButton("Don't use Equalizer APO", "You can turn it back on in Settings.");

        var page = new TaskDialogPage
        {
            Caption = "MicBoost",
            Heading = "Allow MicBoost to use Equalizer APO?",
            Text = "Equalizer APO keeps its configuration in the Program Files folder, so connecting MicBoost " +
                   "to it needs administrator permission once. After that, boost changes never ask again.",
            Icon = TaskDialogIcon.ShieldBlueBar,
            Buttons = { yes, later, never },
        };

        var result = TaskDialog.ShowDialog(page);
        return result == yes ? ApoSetupAnswer.Continue : result == never ? ApoSetupAnswer.Never : ApoSetupAnswer.NotNow;
    }
}
