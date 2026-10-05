namespace LogicScope.Core.Models;

public enum CaptureMode : byte
{
    Streaming = 0,
    Burst = 1
}

public enum TriggerEdge : byte
{
    Falling = 0,
    Rising = 1
}

public sealed record CaptureSettings(
    uint SampleRateHz,
    CaptureMode Mode,
    byte TriggerChannel = 0xFF,
    TriggerEdge Edge = TriggerEdge.Rising,
    byte PreTriggerPercent = 0)
{
    public void Validate()
    {
        if (SampleRateHz == 0) throw new ArgumentOutOfRangeException(nameof(SampleRateHz));
        if (TriggerChannel != 0xFF && TriggerChannel >= 16)
            throw new ArgumentOutOfRangeException(nameof(TriggerChannel));
        if (PreTriggerPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(PreTriggerPercent));
        if (Edge is not TriggerEdge.Falling and not TriggerEdge.Rising)
            throw new ArgumentOutOfRangeException(nameof(Edge));
        if (Mode is not CaptureMode.Streaming and not CaptureMode.Burst)
            throw new ArgumentOutOfRangeException(nameof(Mode));
    }
}

public sealed record DeviceCapabilities(
    uint MaxBurstRateHz,
    uint MaxStreamRateHz,
    uint RamSamples,
    byte FirmwareMajor,
    byte FirmwareMinor,
    byte FirmwarePatch,
    byte HardwareChannels)
{
    public string FirmwareVersion => $"{FirmwareMajor}.{FirmwareMinor}.{FirmwarePatch}";
}

public sealed record DataPacket(
    byte Sequence,
    CaptureMode Mode,
    byte Flags,
    ushort[] Samples)
{
    public bool IsFinal => (Flags & 0x01) != 0;
    public bool Triggered => (Flags & 0x02) != 0;
    public bool DmaOverrun => (Flags & 0x04) != 0;
    public bool SelfTestActive => (Flags & 0x08) != 0;
}

public sealed class DeviceStatus(byte state, byte lastError) : EventArgs
{
    public byte State { get; } = state;
    public byte LastError { get; } = lastError;
}

public sealed record ChannelDefinition(int Index, string Name, string Color, bool IsVisible = true)
{
    private static readonly string[] Palette =
    [
        "#35C7F3", "#F3B33D", "#7DDB82", "#E66B72",
        "#B694F4", "#45D6BC", "#F18BC1", "#A8C76A",
        "#53A9FF", "#DB8A50", "#61C2A5", "#E8D263",
        "#8C9EFF", "#D68EF0", "#73D5E8", "#F28B82"
    ];

    public static IReadOnlyList<ChannelDefinition> DefaultChannels { get; } =
        Enumerable.Range(0, 16)
            .Select(i => new ChannelDefinition(i, $"D{i}", Palette[i]))
            .ToArray();
}

public sealed record BusDefinition(string Name, IReadOnlyList<int> Channels)
{
    public ushort ReadValue(ushort sample)
    {
        ushort value = 0;
        for (var bit = 0; bit < Channels.Count && bit < 16; bit++)
        {
            var channel = Channels[bit];
            if ((uint)channel < 16 && (sample & (1 << channel)) != 0)
                value |= (ushort)(1 << bit);
        }
        return value;
    }
}

public sealed record DecoderResult(
    long StartSample,
    long EndSample,
    string Label,
    string Category = "data",
    bool IsError = false);

public sealed record SampleWindow(long StartSample, uint SampleRateHz, ReadOnlyMemory<ushort> Samples)
{
    public long EndSampleExclusive => StartSample + Samples.Length;
    public double DurationSeconds => Samples.Length / (double)SampleRateHz;
    public double SampleTimeSeconds(long offset) =>
        (StartSample + offset) / (double)SampleRateHz;
}
