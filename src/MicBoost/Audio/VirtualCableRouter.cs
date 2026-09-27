using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MicBoost.Core;

namespace MicBoost.Audio;

/// <summary>
/// Optional third boost method: captures the real microphone with WASAPI, applies gain
/// plus a soft limiter, and plays the result into a virtual audio cable (e.g. VB-CABLE's
/// "CABLE Input"). Other apps then pick the cable's capture side ("CABLE Output") as their mic.
///
/// Latency budget (shared mode, event driven): ~10 ms capture period + a jitter buffer kept
/// below 10 ms + ~10 ms render buffer, which lands under 20 ms end to end on typical hardware.
/// </summary>
public sealed class VirtualCableRouter : IDisposable
{
    private const int CapturePeriodMs = 10;
    private const int RenderLatencyMs = 10;
    private const int MaxQueuedMs = 10; // anything beyond this is clock drift - drop it

    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private GainLimiterSampleProvider? _gain;
    private volatile bool _stopping;

    /// <summary>Raised (on an audio thread) when the stream dies, e.g. the device was unplugged.</summary>
    public event EventHandler<string>? Failed;

    public bool IsRunning => _output?.PlaybackState == PlaybackState.Playing;
    public string? CableName { get; private set; }
    public string? SourceDeviceId { get; private set; }

    /// <summary>Gain in dB applied before the limiter.</summary>
    public float GainDb
    {
        get => _gainDb;
        set
        {
            _gainDb = Math.Clamp(value, 0f, 40f);
            if (_gain != null) _gain.LinearGain = MathF.Pow(10f, _gainDb / 20f);
        }
    }
    private float _gainDb;

    /// <summary>Post-gain, pre-limiter peak since the last read; used for the popup meter.</summary>
    public float ReadPeak() => _gain?.ReadAndResetPeak() ?? 0f;

    /// <summary>Render endpoints that look like virtual cables.</summary>
    public static List<(string Id, string Name)> FindCables(MMDeviceEnumerator enumerator)
    {
        var result = new List<(string, string)>();
        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var name = device.FriendlyName;
                    if (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add((device.ID, name));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Enumerating render devices failed: {ex.Message}");
        }
        // Prefer VB-CABLE's own endpoint when auto-detecting.
        return result.OrderByDescending(c => c.Item2.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Starts routing <paramref name="source"/> into <paramref name="cable"/>.</summary>
    public void Start(MMDevice source, MMDevice cable)
    {
        Stop();
        _stopping = false;
        try
        {
            _capture = new WasapiCapture(source, useEventSync: true, audioBufferMillisecondsLength: CapturePeriodMs);
            var captureFormat = _capture.WaveFormat;

            _buffer = new BufferedWaveProvider(captureFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(200),
                DiscardOnBufferOverflow = true,
                ReadFully = true, // output silence on underrun instead of stopping
            };

            // Build: capture -> gain/limiter -> channel match -> resample -> cable.
            ISampleProvider chain = _buffer.ToSampleProvider();
            _gain = new GainLimiterSampleProvider(chain);
            chain = _gain;
            GainDb = _gainDb;

            // Match the cable's shared-mode mix format: resample first (fewer channels to
            // process for a mono mic), then map channels (mono -> stereo duplicates the signal).
            var mix = cable.AudioClient.MixFormat;
            if (chain.WaveFormat.SampleRate != mix.SampleRate)
                chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);

            int inChannels = chain.WaveFormat.Channels;
            if (inChannels != mix.Channels)
            {
                var mux = new MultiplexingSampleProvider(new[] { chain }, mix.Channels);
                for (int output = 0; output < mix.Channels; output++)
                    mux.ConnectInputToOutput(output % inChannels, output);
                chain = mux;
            }

            _output = new WasapiOut(cable, AudioClientShareMode.Shared, useEventSync: true, RenderLatencyMs);
            _output.Init(new SampleToWaveProvider(chain));
            _output.PlaybackStopped += OnStopped;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnStopped;

            _capture.StartRecording();
            _output.Play();

            CableName = cable.FriendlyName;
            SourceDeviceId = source.ID;
            Log.Info($"Virtual cable routing started: '{source.FriendlyName}' -> '{CableName}' " +
                     $"({captureFormat.SampleRate} Hz/{captureFormat.Channels}ch -> {mix.SampleRate} Hz/{mix.Channels}ch)");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var buffer = _buffer;
        if (buffer == null) return;

        // Keep latency bounded: if the render side has fallen behind (clock drift between
        // the two devices), drop the queued audio instead of letting it grow.
        if (buffer.BufferedDuration.TotalMilliseconds > MaxQueuedMs + CapturePeriodMs)
            buffer.ClearBuffer();

        buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        // NAudio posts stop events asynchronously; ignore ones from streams we already replaced.
        if (_stopping || (!ReferenceEquals(sender, _capture) && !ReferenceEquals(sender, _output))) return;
        var reason = e.Exception?.Message ?? "The audio stream stopped.";
        Log.Warn($"Virtual cable routing stopped: {reason}");
        Failed?.Invoke(this, reason);
    }

    public void Stop()
    {
        _stopping = true;
        try { _capture?.StopRecording(); } catch { /* device gone */ }
        try { _output?.Stop(); } catch { /* device gone */ }
        try { _capture?.Dispose(); } catch { /* device gone */ }
        try { _output?.Dispose(); } catch { /* device gone */ }
        _capture = null;
        _output = null;
        _buffer = null;
        _gain = null;
        CableName = null;
        SourceDeviceId = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Applies a linear gain followed by a stateless soft-knee limiter: samples below the knee
/// pass unchanged; above it they are compressed with tanh so the output approaches, but
/// never exceeds, full scale. No look-ahead, so it adds zero latency.
/// </summary>
public sealed class GainLimiterSampleProvider : ISampleProvider
{
    private const float Knee = 0.80f;           // about -1.9 dBFS
    private const float Headroom = 1f - Knee;
    private readonly ISampleProvider _source;
    private float _peak;

    public GainLimiterSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    private volatile float _linearGain = 1f;

    public float LinearGain { get => _linearGain; set => _linearGain = value; }

    /// <summary>
    /// Highest pre-limiter sample magnitude since the last call. Values at or above 1.0 mean
    /// the signal would have clipped and the limiter is working - the meter shows that as red.
    /// </summary>
    public float ReadAndResetPeak() => Interlocked.Exchange(ref _peak, 0f);

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float gain = _linearGain;
        float peak = 0f;

        for (int i = offset; i < offset + read; i++)
        {
            float x = buffer[i] * gain;
            float ax = MathF.Abs(x);
            if (ax > peak) peak = ax;
            if (ax > Knee)
                x = MathF.CopySign(Knee + Headroom * MathF.Tanh((ax - Knee) / Headroom), x);
            buffer[i] = x;
        }

        // Keep the maximum until the UI reads it.
        float current;
        do
        {
            current = _peak;
            if (peak <= current) break;
        } while (Interlocked.CompareExchange(ref _peak, peak, current) != current);

        return read;
    }
}
