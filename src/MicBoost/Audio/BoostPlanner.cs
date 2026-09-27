namespace MicBoost.Audio;

/// <summary>Supported range of a hardware boost control, with dB step helpers.</summary>
public readonly record struct HardwareBoostRange(float MinDb, float MaxDb, float StepDb)
{
    /// <summary>Step used when a driver reports 0 (continuous control).</summary>
    public float EffectiveStep => StepDb > 0.01f ? StepDb : 0.5f;

    /// <summary>Boost level that means "no boost": the step closest to 0 dB.</summary>
    public float NeutralDb => Snap(0f);

    /// <summary>Largest boost this control can add on top of <see cref="NeutralDb"/>.</summary>
    public float MaxContributionDb => MaxDb - NeutralDb;

    /// <summary>Nearest supported step inside the range.</summary>
    public float Snap(float db)
    {
        float clamped = Math.Clamp(db, MinDb, MaxDb);
        float steps = MathF.Round((clamped - MinDb) / EffectiveStep, MidpointRounding.AwayFromZero);
        return Math.Clamp(MinDb + steps * EffectiveStep, MinDb, MaxDb);
    }

    /// <summary>Largest supported step that does not exceed <paramref name="db"/>.</summary>
    public float FloorStep(float db)
    {
        float clamped = Math.Clamp(db, MinDb, MaxDb);
        float steps = MathF.Floor((clamped - MinDb) / EffectiveStep + 0.001f);
        return Math.Clamp(MinDb + steps * EffectiveStep, MinDb, MaxDb);
    }
}

/// <summary>How a requested boost is split across the available methods.</summary>
/// <param name="DesiredDb">Boost the slider asks for.</param>
/// <param name="HardwareLevelDb">Absolute level to set on the hardware control (NaN if none).</param>
/// <param name="HardwareDb">Hardware contribution above neutral.</param>
/// <param name="SoftwareDb">Gain for the software stage (APO preamp or cable gain).</param>
/// <param name="Limited">True if the desired boost is beyond what the available methods can reach.</param>
public readonly record struct BoostPlan(float DesiredDb, float HardwareLevelDb, float HardwareDb, float SoftwareDb, bool Limited)
{
    public float TotalDb => HardwareDb + SoftwareDb;
}

/// <summary>
/// Pure mapping from the 0..200 % slider to device settings (no Core Audio calls, unit tested).
///
///   0..100 %   endpoint scalar = level / 100, no boost
///   100..200 % endpoint scalar = 1, boost = (level - 100) / 100 * maxBoostDb
///
/// The boost goes to the hardware Microphone Boost first. If a software stage exists, the
/// hardware step is rounded down and software adds the exact remainder; without one, the
/// hardware step is rounded to the nearest supported value.
/// </summary>
public static class BoostPlanner
{
    public const float MaxSoftwareDb = 40f;

    public static float EndpointScalar(double levelPercent) =>
        (float)(Math.Clamp(levelPercent, 0, 100) / 100.0);

    public static float DesiredBoostDb(double levelPercent, double maxBoostDb) =>
        levelPercent <= 100 ? 0f : (float)((Math.Min(levelPercent, 200) - 100) / 100.0 * maxBoostDb);

    public static BoostPlan Plan(float desiredDb, HardwareBoostRange? hardware, bool softwareAvailable)
    {
        desiredDb = Math.Max(0f, desiredDb);
        float hwLevel = float.NaN, hwDb = 0f;

        if (hardware is { } hw)
        {
            float neutral = hw.NeutralDb;
            float target = neutral + desiredDb;
            float step = softwareAvailable ? hw.FloorStep(target) : hw.Snap(target);
            hwLevel = Math.Max(step, neutral);
            hwDb = hwLevel - neutral;
        }

        float softDb = softwareAvailable ? Math.Clamp(desiredDb - hwDb, 0f, MaxSoftwareDb) : 0f;
        float reachable = (hardware?.MaxContributionDb ?? 0f) + (softwareAvailable ? MaxSoftwareDb : 0f);
        return new BoostPlan(desiredDb, hwLevel, hwDb, softDb, desiredDb > reachable + 0.05f);
    }
}
