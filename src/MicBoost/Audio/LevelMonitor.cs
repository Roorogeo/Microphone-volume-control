using NAudio.CoreAudioApi;
using NAudio.Wave;
using MicBoost.Core;

namespace MicBoost.Audio;

/// <summary>
/// Measures the live input peak for the popup's level meter.
///
/// IAudioMeterInformation on a capture endpoint only reports levels while some stream is
/// open, so while the popup is visible we open a lightweight shared-mode capture and take
/// the peak of each buffer ourselves. The captured signal already includes the endpoint
/// volume, the hardware boost and any Equalizer APO preamp, so the meter shows exactly
/// what other apps receive. The stream is closed as soon as the popup hides.
/// </summary>
public sealed class LevelMonitor : IDisposable
{
    private WasapiCapture? _capture;
    private float _peak;

    public bool IsRunning => _capture != null;

    public bool Start(MMDevice device)
    {
        Stop();
        try
        {
            _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            // Typical causes: microphone privacy setting off, device in exclusive use.
            Log.Warn($"Level meter capture failed: {ex.Message}");
            Stop();
            return false;
        }
    }

    /// <summary>Highest sample magnitude (0..1) since the previous call.</summary>
    public float ReadAndResetPeak() => Interlocked.Exchange(ref _peak, 0f);

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _capture?.WaveFormat;
        if (format == null || e.BytesRecorded == 0) return;

        float peak = 0f;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32))
        {
            // The shared-mode mix format is 32-bit float in practice.
            var samples = MemoryMarshalHelpers.AsFloats(e.Buffer, e.BytesRecorded);
            foreach (var s in samples)
            {
                float a = MathF.Abs(s);
                if (a > peak) peak = a;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            var samples = MemoryMarshalHelpers.AsShorts(e.Buffer, e.BytesRecorded);
            foreach (var s in samples)
            {
                float a = Math.Abs(s / 32768f);
                if (a > peak) peak = a;
            }
        }

        float current;
        do
        {
            current = _peak;
            if (peak <= current) break;
        } while (Interlocked.CompareExchange(ref _peak, peak, current) != current);
    }

    public void Stop()
    {
        if (_capture == null) return;
        var capture = _capture;
        _capture = null;
        try { capture.StopRecording(); } catch { /* device gone */ }
        try { capture.Dispose(); } catch { /* device gone */ }
        _peak = 0f;
    }

    public void Dispose() => Stop();
}

internal static class MemoryMarshalHelpers
{
    public static ReadOnlySpan<float> AsFloats(byte[] buffer, int bytes) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytes - bytes % 4));

    public static ReadOnlySpan<short> AsShorts(byte[] buffer, int bytes) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytes - bytes % 2));
}
