using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LogicScope.Core.Acquisition;
using LogicScope.Core.Interfaces;
using LogicScope.Core.Models;
using Xunit;

namespace LogicScope.Tests;

public sealed class AcquisitionEngineTests
{
    [Fact]
    public async Task StopDrainsQueuedPacketsAndCountsSequenceGaps()
    {
        var device = new FakeDevice();
        var ring = new SampleRingBuffer(512);
        await using var engine = new AcquisitionEngine(device, ring);
        await engine.StartAsync(new CaptureSettings(1_000_000, CaptureMode.Streaming));

        device.Publish(new DataPacket(0, CaptureMode.Streaming, 0, new ushort[] { 1, 2 }));
        device.Publish(new DataPacket(2, CaptureMode.Streaming, 0x0E, new ushort[] { 3, 4 }));
        await engine.StopAsync();

        Assert.Equal(4L, engine.SamplesReceived);
        Assert.Equal(1L, engine.DroppedPackets);
        Assert.True(engine.Triggered);
        Assert.True(engine.DmaOverrun);
        Assert.True(engine.SelfTestActive);
        Assert.Equal(new ushort[] { 1, 2, 3, 4 },
            ring.ReadWindow(0, 4, 1_000_000).Samples.ToArray());
        Assert.False(engine.IsRunning);
    }

    private sealed class FakeDevice : ILogicScopeDevice
    {
        private readonly Channel<DataPacket> _packets = Channel.CreateUnbounded<DataPacket>();
        private bool _connected = true;

        public string PortName => "FAKE";
        public DeviceCapabilities? Capabilities => null;
        public bool IsConnected => _connected;
        public event EventHandler<DeviceStatus>? StatusReceived;

        public Task<DeviceCapabilities> ConnectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceCapabilities(20_000_000, 10_000_000, 131_072, 1, 0, 0, 16));

        public Task StartAsync(CaptureSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _packets.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public void Publish(DataPacket packet) => _packets.Writer.TryWrite(packet);

        public async IAsyncEnumerable<DataPacket> ReadDataAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var packet in _packets.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
                yield return packet;
        }

        public ValueTask DisposeAsync()
        {
            _connected = false;
            _packets.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void RaiseStatus(DeviceStatus status) => StatusReceived?.Invoke(this, status);
    }
}
