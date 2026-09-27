# MicBoost

A small Windows 10/11 tray app that controls your microphone's input level, including
**boosting it beyond Windows' normal 100 % limit**.

- Lives only in the system tray (no taskbar button, no main window).
- **Left-click** the tray icon for a flyout with a 0–200 % slider, the exact level in % and
  dB, a mute button and a live input meter that turns red when the signal clips.
- **Mouse wheel** over the flyout changes the level in 2 % steps.
- **Right-click** menu: microphone picker (default device marked), Mute, Lock Volume,
  Start with Windows, Settings, Exit.
- The tray icon changes for normal, muted and boosted (> 100 %) states; the tooltip shows the
  device name and current level.
- Global hotkeys (configurable) with a small on-screen display:
  `Ctrl+Alt+PageUp` / `Ctrl+Alt+PageDown` change the level by 5 %, `Ctrl+Alt+M` toggles mute.
- **Lock Volume** puts your level back when Discord, Zoom, Teams or driver auto-gain changes it.
- Follows default-device changes and hot-plugging, remembers level and boost per microphone,
  allows only one instance, and follows the Windows light/dark theme.
- No admin rights needed. The only time it asks for elevation is a one-time Equalizer APO setup.

---

## Build

Requirements: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Build on
Windows. Other operating systems can compile the project (via `EnableWindowsTargeting`) but
can't run it.

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Run it from the repository root or from `src/MicBoost`. The result is one file, no installer:

```
src/MicBoost/bin/Release/net8.0-windows/win-x64/publish/MicBoost.exe
```

Copy `MicBoost.exe` anywhere and run it. Because it is self-contained, the target PC doesn't
need .NET installed.

The exe is about 155 MB because it contains the entire .NET and WinForms runtime (WinForms
can't be trimmed). Adding `-p:EnableCompressionInSingleFile=true` cuts the file roughly in half.
The trade-off is that every assembly is then unpacked into RAM at startup, which works against
the "under 50 MB" memory goal, so compression is off by default.

Unit tests cover the slider-to-dB mapping and the limiter. They run on any OS:

```bash
dotnet test tests/MicBoost.Tests
```

---

## How the boost works

The slider runs from 0 to 200 %. The two halves are handled very differently:

| Slider      | What MicBoost changes                                                           |
|-------------|---------------------------------------------------------------------------------|
| 0 – 100 %   | The endpoint master volume (`IAudioEndpointVolume` scalar 0.0–1.0). This is the same slider Windows shows. |
| 100 – 200 % | Endpoint stays at 100 %, and a **boost** is added: `boost dB = (level − 100) / 100 × MaxBoost`. |

`MaxBoost` defaults to **30 dB** and can be changed in Settings (3–40 dB). With the default,
110 % = +3 dB, 150 % = +15 dB and 200 % = +30 dB. The flyout always shows the exact dB that was
applied, including how it is split, for example `Boost +23.0 dB (HW +20 · APO +3.0)`.

MicBoost builds the boost from up to three sources, in this order:

### 1. Hardware Microphone Boost (driver)

Many onboard audio codecs (Realtek, Conexant, …) have a "Microphone Boost" gain stage. Windows
shows it in *Sound settings → Recording → Properties → Levels*. MicBoost finds it through the
Device Topology API:

1. Activate `IDeviceTopology` on the capture endpoint.
2. Follow the endpoint's connector to the adapter's KS filter topology (`IConnector.GetConnectedTo`).
3. Walk the signal path upstream (`IPart.EnumPartsIncoming`, towards the mic jack) and collect
   every part that exposes an `IAudioVolumeLevel` control.
4. Pick the part named "…Boost…". If none has that name, pick a gain-only range (e.g. 0…+30 dB)
   that is not the master volume node.

The control only accepts fixed steps, typically 0/+10/+20/+30 dB, so MicBoost always sets it to
a supported step. Hardware gain costs no CPU and is applied before the analog-to-digital
converter, which usually gives the cleanest result. Many USB microphones and headsets have no
such control. For those, MicBoost moves straight to step 2.

### 2. Software gain via Equalizer APO

[Equalizer APO](https://sourceforge.net/projects/equalizerapo/) is a free, open-source audio
processing object. Once it is installed on a microphone, it applies whatever its `config.txt`
says to every app that records from that mic, and it reloads the file automatically when the
file changes.

MicBoost adds exactly one line to `config.txt`:

```
Include: MicBoost.txt
```

and writes its own `MicBoost.txt` in the same folder:

```
# Written by MicBoost - changes to this file are overwritten.
# Microphone (Realtek(R) Audio)
Device: {c3d5d0a0-8f5e-4b6a-9d0e-1a2b3c4d5e6f}
Preamp: +3.0 dB

Device: all
```

The `Device:` line limits the preamp to that one microphone (matched by its endpoint GUID). The
trailing `Device: all` makes sure nothing in your own config is affected. Each microphone keeps
its own block, so switching mics in MicBoost doesn't remove the boost from the other one.

When a software stage is available, the hardware step is rounded **down** and Equalizer APO adds
the exact remainder. For example, +23 dB becomes HW +20 dB plus APO +3.0 dB. Without a software
stage, the hardware step is rounded to the nearest value, and the dB readout shows what was
actually reached.

If you push past the hardware limit and Equalizer APO is missing, MicBoost shows a one-time
explanation with a download link.

### 3. Optional fallback: virtual audio cable

For setups where Equalizer APO can't be used, MicBoost can route the microphone through a
virtual cable such as [VB-CABLE](https://vb-audio.com/Cable/):

```
real mic ──WASAPI capture──► gain + soft limiter ──WASAPI render──► "CABLE Input"
                                                                          │
               Discord / Zoom / OBS choose "CABLE Output" as their mic ◄──┘
```

- Both sides run in shared, event-driven mode with 10 ms buffers. The jitter buffer between
  them is capped, so end-to-end latency stays below about 20 ms.
- The soft limiter passes the signal unchanged below about −2 dBFS. Above that it rounds peaks
  off with a `tanh` curve, so even +30 dB of gain never hard-clips. It has no look-ahead and adds
  no latency.
- Sample-rate and channel differences between the mic and the cable are converted automatically.
- While this fallback is enabled, MicBoost keeps routing even at ≤ 100 % (with 0 dB gain), so the
  apps listening on the cable never lose audio.

It is off by default. Enable it in **Settings → Boost → Virtual cable fallback**.

---

## Setting up Equalizer APO

1. **Download** Equalizer APO from <https://sourceforge.net/projects/equalizerapo/> and run the installer.
2. At the end of setup the **Configurator** opens. On the **Capture devices** tab, tick the
   microphone(s) you want to boost and click **OK**.
   *Tip:* if the boost later has no effect on that mic, open the Configurator again
   (`C:\Program Files\EqualizerAPO\Configurator.exe`), select the mic, tick
   *Troubleshooting options* and try *Install as SFX/EFX (experimental)*.
3. **Restart Windows.** Equalizer APO only attaches to a device after a reboot.
4. In MicBoost, drag the slider past the hardware limit, or open **Settings → Set up Equalizer APO…**.
   Windows shows **one** UAC prompt. The elevated helper (`MicBoost.exe --apo-setup`):
   - creates `MicBoost.txt` in Equalizer APO's config folder,
   - gives your user account write access to **that file only**,
   - appends `Include: MicBoost.txt` to `config.txt` (only if it isn't there yet).

   After that, every boost change is written without any prompt.
5. **Settings** shows the state of each boost method, for example
   `2. Equalizer APO: ready (C:\Program Files\EqualizerAPO\config\MicBoost.txt)`.

**To undo:** delete the `Include: MicBoost.txt` line from `config.txt` and delete `MicBoost.txt`,
or just uncheck *Use Equalizer APO* in MicBoost's settings. That writes an empty `MicBoost.txt`
(no preamp).

> Equalizer APO's preamp is plain gain with no limiter. If the meter turns red, lower the boost.

---

## Other features in detail

- **Lock Volume.** Every change MicBoost makes carries its own event-context GUID. When a volume
  notification arrives with a different GUID (another app, the Windows mixer, driver auto-gain),
  MicBoost restores your level after 150 ms. The hardware boost is watched the same way through
  `IControlChangeNotify`. When Lock is off, MicBoost follows outside changes instead, for example
  when you move the Windows slider.
- **Devices.** `IMMNotificationClient` reports hot-plug and default-device changes. If the chosen
  mic is unplugged, MicBoost falls back to the Windows default, and it switches back
  automatically when the mic returns. *Always use Windows default* in the menu follows the
  default device instead.
- **Settings file:** `%AppData%\MicBoost\settings.json` stores level, hardware boost and software
  boost per device (keyed by endpoint ID), plus hotkeys and options. A log for diagnosing boost
  detection is in `%AppData%\MicBoost\micboost.log`.
- **Start with Windows:** `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\MicBoost` (per user, no admin).
- **Single instance:** a named mutex. Launching the exe again opens the running instance's flyout.
- **Theme:** window colours follow *Apps* light/dark mode and your accent colour. The tray icon
  follows the *Windows* (taskbar) mode.
- **Resource use:** with the flyout closed there are no timers, no polling and no open audio
  streams. Everything is driven by Core Audio notifications, so idle CPU is about 0 %. The level
  meter opens a capture stream only while the flyout is visible. Windows therefore shows its
  "microphone in use" indicator during that time.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "Hardware boost: not available" in Settings | The driver has no boost control (common for USB mics). Use Equalizer APO. |
| Equalizer APO "installed, but not enabled for this microphone" | Tick the mic in the Configurator's *Capture devices* tab and reboot. |
| Boost above the hardware limit does nothing | APO isn't attached yet (reboot), or the one-time setup was declined. Use *Settings → Set up Equalizer APO…*. |
| A hotkey doesn't work | Another app already owns that combination. MicBoost shows a notification. Pick another in Settings. |
| Level jumps back after I change it in Windows | *Lock Volume* is on. That is its job. |
| Meter stays empty | Windows privacy setting *Let desktop apps access your microphone* is off. |

## Project layout

```
src/MicBoost/
  Program.cs                   entry point, single-instance mutex, elevated APO helper mode
  TrayApplicationContext.cs    tray icon, menu, hotkeys, dialogs, settings glue
  Audio/
    VolumeController.cs        slider -> endpoint + boost split, Lock Volume, device lifetime
    BoostPlanner.cs            pure dB mapping and hardware/software split (unit tested)
    HardwareBoost.cs           Device Topology walk, IAudioVolumeLevel boost control
    EqualizerApo.cs            detection, MicBoost.txt writer, one-time elevated setup
    VirtualCableRouter.cs      WASAPI capture -> gain/soft limiter -> virtual cable
    LevelMonitor.cs            peak meter capture (only while the flyout is open)
    AudioDeviceManager.cs      device enumeration + IMMNotificationClient
    Interop/TopologyInterop.cs COM declarations for devicetopology.h
  Core/                        settings, hotkeys, autostart, theme, logging
  UI/                          flyout, slider, meter, OSD, settings, dialogs, tray icons
tests/MicBoost.Tests/          xUnit tests for BoostPlanner and the limiter
```
