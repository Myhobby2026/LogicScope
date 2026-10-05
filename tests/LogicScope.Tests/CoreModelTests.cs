using LogicScope.Core.Acquisition;
using LogicScope.Core.Measurements;
using LogicScope.Core.Models;
using Xunit;

namespace LogicScope.Tests;

public sealed class CoreModelTests
{
    [Fact]
    public void SampleRingOverwritesOldPagesAndAggregatesVisibleData()
    {
        var ring = new SampleRingBuffer(512);
        var values = Enumerable.Range(0, 600).Select(i => (ushort)(i & 1)).ToArray();
        ring.Append(values);
        Assert.Equal(88L, ring.OldestIndex);
        Assert.Equal(600L, ring.NextIndex);
        Assert.False(ring.TryGetSample(87, out _));
        Assert.True(ring.TryGetSample(88, out var value));
        Assert.Equal((ushort)0, value);

        var envelope = ring.AggregateVisible(88, 512, 4);
        Assert.Equal(4, envelope.Length);
        Assert.All(envelope, item =>
        {
            Assert.True(item.HasSamples);
            Assert.Equal((ushort)1, item.AnyHigh);
            Assert.NotEqual((ushort)0, (ushort)(item.AnyLow & 1));
        });
    }

    [Fact]
    public void TriggerScannersFindFirstPatternAndEdgeInAbsoluteSampleSpace()
    {
        var ring = new SampleRingBuffer(512);
        ring.Append(new ushort[] { 0b0010, 0b0011, 0xA503, 0xA502, 0b0000 });
        Assert.Equal((long?)2, ring.FindPattern(TriggerPattern.Parse("A5XX", "FFFF"), 0, 5));
        Assert.Equal((long?)1, ring.FindEdge(0, TriggerEdge.Rising, 0, 5));
        Assert.Equal((long?)4, ring.FindEdge(0, TriggerEdge.Falling, 1, 5));
    }

    [Fact]
    public void MeasurementsFindPeriodDutyAndCursors()
    {
        // A 100 kHz clock represented by ten samples per period, high for four.
        var samples = new ushort[100];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (ushort)((i % 10) < 4 ? 1 : 0);
        var window = new SampleWindow(0, 1_000_000, samples);
        var measured = DigitalMeasurements.AnalyzeChannel(window, 0);
        Assert.InRange(measured.FrequencyHz!.Value, 99_000, 101_000);
        Assert.InRange(measured.DutyCycle!.Value, 0.38, 0.42);
        Assert.Equal(0.000004, measured.MeanHighSeconds!.Value, 9);
        Assert.True(measured.EstimatedRiseTimeSeconds > 0);

        var cursors = DigitalMeasurements.MeasureCursors(window, 0.000002, 0.000007, 0, 0);
        Assert.Equal(0.000005, cursors.DeltaSeconds, 12);
    }

    [Fact]
    public void BusDefinitionPacksConfiguredLogicalChannels()
    {
        var bus = new BusDefinition("DATA", new[] { 0, 2, 4, 6 });
        Assert.Equal((ushort)0b1101, bus.ReadValue(0b0101_0001));
    }
}
