using LogicScope.Core.Models;

namespace LogicScope.Core.Interfaces;

public interface ILogicScopeDevice : IAsyncDisposable
{
    string PortName { get; }
    DeviceCapabilities? Capabilities { get; }
    bool IsConnected { get; }
    event EventHandler<DeviceStatus>? StatusReceived;

    Task<DeviceCapabilities> ConnectAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CaptureSettings settings, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<DataPacket> ReadDataAsync(CancellationToken cancellationToken = default);
}
