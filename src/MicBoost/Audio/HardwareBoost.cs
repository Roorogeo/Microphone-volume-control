using System.Runtime.InteropServices;
using MicBoost.Audio.Interop;
using MicBoost.Core;

namespace MicBoost.Audio;

/// <summary>
/// Wraps the driver's hardware "Microphone Boost" control - the extra slider shown in
/// Sound settings > Recording > Properties > Levels.
///
/// How it is found: the capture endpoint's own device topology has a single connector
/// that is wired to the audio adapter's KS filter topology. From that adapter-side
/// connector we walk the signal path *upstream* (IPart.EnumPartsIncoming, towards the
/// microphone jack) and collect every part that exposes an IAudioVolumeLevel control.
/// The boost is the part whose name contains "boost"; if no part is named that way we
/// fall back to a non-negative dB range (e.g. 0..+30 dB) that is not the first volume
/// node next to the endpoint (that one is normally the master volume).
/// </summary>
public sealed class HardwareBoost : IDisposable
{
    private readonly IPart _part;
    private readonly IAudioVolumeLevel _level;
    private readonly List<object> _comRefs;
    private readonly uint _channels;
    private readonly ControlChangeSink? _sink;
    private Guid _eventContext;
    private bool _disposed;

    /// <summary>Raised (on a COM worker thread) when another process changes the boost.</summary>
    public event EventHandler? ChangedExternally;

    public string Name { get; }

    /// <summary>Supported dB range and step size reported by the driver.</summary>
    public HardwareBoostRange Range { get; }

    public float NeutralDb => Range.NeutralDb;
    public float MaxContributionDb => Range.MaxContributionDb;

    private HardwareBoost(IPart part, IAudioVolumeLevel level, string name, uint channels,
                          float min, float max, float step, List<object> comRefs, Guid eventContext)
    {
        _part = part;
        _level = level;
        _comRefs = comRefs;
        _channels = channels;
        _eventContext = eventContext;
        Name = name;
        // Some drivers report a stepping of 0 for continuous controls; the range handles that.
        Range = new HardwareBoostRange(min, max, step);

        // Get told when something else (the Levels tab, another app) changes the boost,
        // so Lock Volume can put it back. Failure here is harmless.
        try
        {
            var sink = new ControlChangeSink(this);
            var iid = ComIids.IAudioVolumeLevel;
            if (_part.RegisterControlChangeCallback(ref iid, sink) >= 0)
                _sink = sink;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not register boost change callback: {ex.Message}");
        }
    }

    /// <summary>Current boost in dB (channel 0).</summary>
    public float CurrentDb
    {
        get
        {
            if (_disposed) return NeutralDb;
            return _level.GetLevel(0, out float db) >= 0 ? db : NeutralDb;
        }
    }

    /// <summary>Sets the boost (snapped to a supported step) and returns the level applied.</summary>
    public float Set(float db)
    {
        if (_disposed) return NeutralDb;
        float target = Range.Snap(db);
        if (MathF.Abs(CurrentDb - target) < 0.01f) return target;

        int hr = _level.SetLevelUniform(target, ref _eventContext);
        if (hr < 0)
        {
            // Some drivers reject SetLevelUniform; fall back to setting channels one by one.
            for (uint ch = 0; ch < _channels; ch++)
            {
                int chHr = _level.SetLevel(ch, target, ref _eventContext);
                if (chHr < 0) Marshal.ThrowExceptionForHR(chHr);
            }
        }
        return target;
    }

    public override string ToString() =>
        $"{Name}: {Range.MinDb:+0.#;-0.#;0} to {Range.MaxDb:+0.#;-0.#;0} dB in {Range.EffectiveStep:0.#} dB steps";

    /// <summary>
    /// Looks for a hardware boost control on the capture endpoint with the given ID.
    /// Returns null (with a human-readable reason) when the device has none.
    /// </summary>
    public static HardwareBoost? TryOpen(string endpointId, Guid eventContext, out string reason)
    {
        var comRefs = new List<object>();
        try
        {
            var enumerator = (IMMDeviceEnumeratorRaw)new MMDeviceEnumeratorComObject();
            comRefs.Add(enumerator);
            Check(enumerator.GetDevice(endpointId, out var device));
            comRefs.Add(device);

            var topologyIid = ComIids.IDeviceTopology;
            Check(device.Activate(ref topologyIid, ComIids.ClsCtxAll, IntPtr.Zero, out object topologyObj));
            comRefs.Add(topologyObj);
            var endpointTopology = (IDeviceTopology)topologyObj;

            Check(endpointTopology.GetConnectorCount(out uint connectorCount));
            var candidates = new List<Candidate>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (uint i = 0; i < connectorCount; i++)
            {
                if (endpointTopology.GetConnector(i, out var endpointConnector) < 0) continue;
                comRefs.Add(endpointConnector);
                if (((IPart)endpointConnector).GetGlobalId(out string endpointConnectorId) >= 0)
                    visited.Add(endpointConnectorId);

                // Hop from the endpoint topology into the adapter (KS filter) topology.
                if (endpointConnector.GetConnectedTo(out var adapterConnector) < 0) continue;
                comRefs.Add(adapterConnector);
                Walk((IPart)adapterConnector, candidates, visited, comRefs, depth: 0);
            }

            var chosen = Choose(candidates);
            if (chosen == null)
            {
                reason = candidates.Count == 0
                    ? "The driver exposes no volume controls in its topology."
                    : "The driver exposes no Microphone Boost control.";
                ReleaseAll(comRefs);
                return null;
            }

            // Release everything that is not needed to keep the chosen control alive.
            var keep = new List<object> { chosen.Part, chosen.Level };
            ReleaseAll(comRefs.Where(o => !keep.Contains(o)).ToList());

            reason = string.Empty;
            var boost = new HardwareBoost(chosen.Part, chosen.Level, chosen.Name, chosen.Channels,
                                          chosen.Min, chosen.Max, chosen.Step, keep, eventContext);
            Log.Info($"Hardware boost found: {boost}");
            return boost;
        }
        catch (Exception ex)
        {
            ReleaseAll(comRefs);
            reason = $"Device topology unavailable ({ex.Message}).";
            Log.Warn($"Hardware boost lookup failed for {endpointId}: {ex}");
            return null;
        }
    }

    // ------------------------------------------------------------------------------

    private sealed record Candidate(IPart Part, IAudioVolumeLevel Level, string Name, uint Channels,
                                    float Min, float Max, float Step, int Order);

    /// <summary>Depth-first walk upstream from <paramref name="part"/>, collecting volume controls.</summary>
    private static void Walk(IPart part, List<Candidate> candidates, HashSet<string> visited,
                             List<object> comRefs, int depth)
    {
        if (depth > 64) return; // defensive: malformed topologies
        if (part.GetGlobalId(out string id) < 0 || !visited.Add(id)) return;

        TryAddCandidate(part, candidates, comRefs);

        // Follow connectors that link to another filter (multi-filter drivers).
        if (part.GetPartType(out var type) >= 0 && type == PartType.Connector && depth > 0 &&
            part is IConnector connector &&
            connector.IsConnected(out bool connected) >= 0 && connected &&
            connector.GetConnectedTo(out var other) >= 0)
        {
            comRefs.Add(other);
            Walk((IPart)other, candidates, visited, comRefs, depth + 1);
        }

        if (part.EnumPartsIncoming(out var incoming) < 0) return; // E_NOTFOUND: start of path
        comRefs.Add(incoming);
        if (incoming.GetCount(out uint count) < 0) return;
        for (uint i = 0; i < count; i++)
        {
            if (incoming.GetPart(i, out var next) < 0) continue;
            comRefs.Add(next);
            Walk(next, candidates, visited, comRefs, depth + 1);
        }
    }

    private static void TryAddCandidate(IPart part, List<Candidate> candidates, List<object> comRefs)
    {
        // Check the control interface list first; activating blindly would throw
        // E_NOINTERFACE for most parts.
        if (part.GetControlInterfaceCount(out uint controlCount) < 0) return;
        bool hasVolume = false;
        for (uint c = 0; c < controlCount && !hasVolume; c++)
        {
            if (part.GetControlInterface(c, out var control) < 0) continue;
            hasVolume = control.GetIID(out Guid iid) >= 0 && iid == ComIids.IAudioVolumeLevel;
            Marshal.ReleaseComObject(control);
        }
        if (!hasVolume) return;

        var levelIid = ComIids.IAudioVolumeLevel;
        if (part.Activate(ComIids.ClsCtxAll, ref levelIid, out object levelObj) < 0) return;
        comRefs.Add(levelObj);
        var level = (IAudioVolumeLevel)levelObj;

        if (level.GetChannelCount(out uint channels) < 0 || channels == 0) return;
        if (level.GetLevelRange(0, out float min, out float max, out float step) < 0) return;
        part.GetName(out string name);

        candidates.Add(new Candidate(part, level, name ?? string.Empty, channels, min, max, step, candidates.Count));
        Log.Info($"  topology volume part #{candidates.Count - 1} '{name}': {min}..{max} dB step {step}");
    }

    private static Candidate? Choose(List<Candidate> candidates)
    {
        // 1) Explicitly named boost controls ("Microphone Boost", "Mic Boost", "Boost").
        var named = candidates.FirstOrDefault(c =>
            c.Name.Contains("boost", StringComparison.OrdinalIgnoreCase) && c.Max > c.Min);
        if (named != null) return named;

        // 2) Unnamed: a gain-only range (min >= 0, max > 0) that is not the master volume
        //    node sitting right next to the endpoint (index 0 on the walk).
        return candidates.FirstOrDefault(c => c.Order > 0 && c.Min >= -0.01f && c.Max >= 6f);
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static void ReleaseAll(IEnumerable<object> objects)
    {
        foreach (var o in objects.Distinct())
        {
            try { if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            catch { /* already released */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_sink != null)
        {
            try { _part.UnregisterControlChangeCallback(_sink); } catch { /* device gone */ }
        }
        ReleaseAll(_comRefs);
    }

    /// <summary>COM callback object for IControlChangeNotify.</summary>
    private sealed class ControlChangeSink : IControlChangeNotify
    {
        private readonly WeakReference<HardwareBoost> _owner;
        public ControlChangeSink(HardwareBoost owner) => _owner = new WeakReference<HardwareBoost>(owner);

        public int OnNotify(uint senderProcessId, IntPtr eventContext)
        {
            // Ignore our own changes; only react to other processes.
            if (senderProcessId != (uint)Environment.ProcessId &&
                _owner.TryGetTarget(out var owner) && !owner._disposed)
            {
                owner.ChangedExternally?.Invoke(owner, EventArgs.Empty);
            }
            return 0;
        }
    }
}
