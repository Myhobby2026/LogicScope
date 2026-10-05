namespace LogicScope.Core.Models;

public sealed class CaptureMetrics : EventArgs
{
    public CaptureMetrics(long samplesReceived, long droppedPackets,
        double samplesPerSecond, double megabytesPerSecond, bool isRunning,
        bool triggered = false, bool dmaOverrun = false, bool selfTestActive = false)
    {
        SamplesReceived = samplesReceived;
        DroppedPackets = droppedPackets;
        SamplesPerSecond = samplesPerSecond;
        MegabytesPerSecond = megabytesPerSecond;
        IsRunning = isRunning;
        Triggered = triggered;
        DmaOverrun = dmaOverrun;
        SelfTestActive = selfTestActive;
    }

    public long SamplesReceived { get; }
    public long DroppedPackets { get; }
    public double SamplesPerSecond { get; }
    public double MegabytesPerSecond { get; }
    public bool IsRunning { get; }
    public bool Triggered { get; }
    public bool DmaOverrun { get; }
    public bool SelfTestActive { get; }
}
