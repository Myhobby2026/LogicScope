using LogicScope.Core.Models;

namespace LogicScope.Core.Measurements;

public sealed record ChannelMeasurement(
    int Channel,
    double? FrequencyHz,
    double? PeriodSeconds,
    double? MeanHighSeconds,
    double? DutyCycle,
    double? MinPulseSeconds,
    double? MaxPulseSeconds,
    double? EstimatedRiseTimeSeconds,
    int RisingEdges,
    int FallingEdges);

public sealed record CursorMeasurement(
    double CursorASeconds,
    double CursorBSeconds,
    double DeltaSeconds,
    bool? ChannelAAtA,
    bool? ChannelAAtB,
    bool? ChannelBAtA,
    bool? ChannelBAtB);

public static class DigitalMeasurements
{
    public static ChannelMeasurement AnalyzeChannel(SampleWindow window, int channel)
    {
        Validate(window, channel);
        var samples = window.Samples.Span;
        if (samples.Length < 2 || window.SampleRateHz == 0)
            return new(channel, null, null, null, null, null, null, null, 0, 0);

        var mask = (ushort)(1u << channel);
        var rising = new List<double>();
        var pulseWidths = new List<double>();
        var highStart = -1d;
        var lowStart = -1d;
        var highDuration = 0d;
        var completeHighDuration = 0d;
        var completeHighPulses = 0;
        var highPulseBeganAtRisingEdge = false;
        var lowPulseBeganAtFallingEdge = false;
        var risingCount = 0;
        var fallingCount = 0;
        var previousHigh = (samples[0] & mask) != 0;
        var rate = window.SampleRateHz;

        if (previousHigh) highStart = window.StartSample / (double)rate;
        else lowStart = window.StartSample / (double)rate;

        for (var i = 1; i < samples.Length; i++)
        {
            var isHigh = (samples[i] & mask) != 0;
            if (isHigh == previousHigh) continue;

            // Digital input is quantized. Linear interpolation between adjacent
            // low/high sample values places the threshold crossing halfway
            // between the two sample instants.
            var crossing = (window.StartSample + i - 0.5d) / rate;
            if (isHigh)
            {
                risingCount++;
                rising.Add(crossing);
                if (lowStart >= 0 && lowPulseBeganAtFallingEdge)
                {
                    var lowWidth = Math.Max(0, crossing - lowStart);
                    pulseWidths.Add(lowWidth);
                }
                highStart = crossing;
                highPulseBeganAtRisingEdge = true;
                lowStart = -1;
                lowPulseBeganAtFallingEdge = false;
            }
            else
            {
                fallingCount++;
                if (highStart >= 0)
                {
                    var highWidth = Math.Max(0, crossing - highStart);
                    highDuration += highWidth;
                    if (highPulseBeganAtRisingEdge)
                    {
                        completeHighDuration += highWidth;
                        completeHighPulses++;
                        pulseWidths.Add(highWidth);
                    }
                }
                lowStart = crossing;
                lowPulseBeganAtFallingEdge = true;
                highStart = -1;
                highPulseBeganAtRisingEdge = false;
            }
            previousHigh = isHigh;
        }

        var period = MeanDifference(rising);
        var frequency = period is > 0 ? 1d / period : null;
        var observedDuration = samples.Length / (double)rate;
        if (highStart >= 0)
        {
            var windowEnd = (window.StartSample + samples.Length) / (double)rate;
            highDuration += Math.Max(0, windowEnd - highStart);
        }
        var duty = observedDuration > 0 ? highDuration / observedDuration : null;
        var meanHigh = completeHighPulses > 0
            ? completeHighDuration / completeHighPulses
            : null;
        var minPulse = pulseWidths.Count > 0 ? pulseWidths.Min() : null;
        var maxPulse = pulseWidths.Count > 0 ? pulseWidths.Max() : null;
        var riseTime = risingCount > 0 ? 0.8d / rate : null;

        return new ChannelMeasurement(channel, frequency, period, meanHigh, duty,
            minPulse, maxPulse, riseTime, risingCount, fallingCount);
    }

    public static double? ChannelSkewSeconds(SampleWindow window, int firstChannel,
        int secondChannel)
    {
        Validate(window, firstChannel);
        Validate(window, secondChannel);
        var samples = window.Samples.Span;
        if (samples.Length < 2 || window.SampleRateHz == 0) return null;
        var firstEdge = FindFirstEdge(samples, firstChannel);
        var secondEdge = FindFirstEdge(samples, secondChannel);
        if (firstEdge is null || secondEdge is null) return null;
        return (firstEdge.Value - secondEdge.Value) / window.SampleRateHz;
    }

    public static CursorMeasurement MeasureCursors(SampleWindow window,
        double cursorASeconds, double cursorBSeconds, int channelA, int channelB)
    {
        Validate(window, channelA);
        Validate(window, channelB);
        var indexA = (long)Math.Floor(cursorASeconds * window.SampleRateHz) - window.StartSample;
        var indexB = (long)Math.Floor(cursorBSeconds * window.SampleRateHz) - window.StartSample;
        return new CursorMeasurement(cursorASeconds, cursorBSeconds,
            cursorBSeconds - cursorASeconds,
            ReadLevel(window, indexA, channelA), ReadLevel(window, indexB, channelA),
            ReadLevel(window, indexA, channelB), ReadLevel(window, indexB, channelB));
    }

    private static double? FindFirstEdge(ReadOnlySpan<ushort> samples, int channel)
    {
        var mask = (ushort)(1u << channel);
        var prior = (samples[0] & mask) != 0;
        for (var i = 1; i < samples.Length; i++)
        {
            var next = (samples[i] & mask) != 0;
            if (next != prior) return i - 0.5d;
            prior = next;
        }
        return null;
    }

    private static bool? ReadLevel(SampleWindow window, long relativeIndex, int channel)
    {
        if (relativeIndex < 0 || relativeIndex >= window.Samples.Length) return null;
        return (window.Samples.Span[(int)relativeIndex] & (1 << channel)) != 0;
    }

    private static double? MeanDifference(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return null;
        var sum = 0d;
        for (var i = 1; i < values.Count; i++) sum += values[i] - values[i - 1];
        return sum / (values.Count - 1);
    }

    private static void Validate(SampleWindow window, int channel)
    {
        ArgumentNullException.ThrowIfNull(window);
        if ((uint)channel >= 16) throw new ArgumentOutOfRangeException(nameof(channel));
    }
}
