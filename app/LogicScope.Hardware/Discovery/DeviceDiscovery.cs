using LogicScope.Core.Models;
using LogicScope.Hardware.Transport;

namespace LogicScope.Hardware.Discovery;

public static class DeviceDiscovery
{
    public static IReadOnlyList<string> EnumeratePorts() =>
        System.IO.Ports.SerialPort.GetPortNames()
            .OrderBy(PortSortKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Probes every enumerated COM port with the binary GET_CAPS handshake. A
    /// successful COM open is not treated as a LogicScope device until the
    /// byte-exact capability response identifies 16 channels.
    /// </summary>
    public static async Task<(UsbSerialTransport? Device, DeviceCapabilities? Capabilities)>
        FindFirstAsync(IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
    {
        foreach (var portName in EnumeratePorts())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Checking {portName}…");
            var candidate = new UsbSerialTransport(portName);
            try
            {
                var caps = await candidate.ConnectAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report($"LogicScope {caps.FirmwareVersion} found on {portName}");
                return (candidate, caps);
            }
            catch (OperationCanceledException)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidOperationException or TimeoutException)
            {
                progress?.Report($"{portName}: {exception.Message}");
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
        }
        progress?.Report("No LogicScope device responded to GET_CAPS.");
        return (null, null);
    }

    private static string PortSortKey(string port)
    {
        var digits = new string(port.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number.ToString("D6") : port;
    }
}
