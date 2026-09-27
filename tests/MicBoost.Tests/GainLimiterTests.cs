using MicBoost.Audio;
using NAudio.Wave;
using Xunit;

namespace MicBoost.Tests;

public class GainLimiterTests
{
    private sealed class ConstantSource : ISampleProvider
    {
        private readonly float[] _samples;
        public ConstantSource(params float[] samples) => _samples = samples;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _samples.Length);
            Array.Copy(_samples, 0, buffer, offset, n);
            return n;
        }
    }

    private static float[] Process(float gainDb, params float[] input)
    {
        var limiter = new GainLimiterSampleProvider(new ConstantSource(input))
        {
            LinearGain = MathF.Pow(10f, gainDb / 20f),
        };
        var output = new float[input.Length];
        limiter.Read(output, 0, output.Length);
        return output;
    }

    [Fact]
    public void QuietSignals_PassWithExactGain()
    {
        var output = Process(6.0206f, 0.1f, -0.2f);
        Assert.Equal(0.2f, output[0], 3);
        Assert.Equal(-0.4f, output[1], 3);
    }

    [Fact]
    public void LoudSignals_NeverExceedFullScale()
    {
        var output = Process(24f, 0.9f, -0.9f, 0.5f, 1f);
        Assert.All(output, s => Assert.InRange(MathF.Abs(s), 0f, 1f));
        Assert.True(output[0] > 0.95f);   // heavily limited but still loud
        Assert.True(output[1] < -0.95f);  // sign preserved
    }

    [Fact]
    public void Limiter_IsMonotonic()
    {
        var input = Enumerable.Range(0, 200).Select(i => i / 100f).ToArray(); // 0 .. 2.0
        var output = Process(0f, input);
        for (int i = 1; i < output.Length; i++)
            Assert.True(output[i] >= output[i - 1]);
    }

    [Fact]
    public void Peak_ReportsPreLimiterLevel_ForClipDetection()
    {
        var limiter = new GainLimiterSampleProvider(new ConstantSource(0.5f)) { LinearGain = 4f };
        var buffer = new float[1];
        limiter.Read(buffer, 0, 1);
        Assert.Equal(2f, limiter.ReadAndResetPeak(), 3);
        Assert.Equal(0f, limiter.ReadAndResetPeak(), 3);
    }
}
