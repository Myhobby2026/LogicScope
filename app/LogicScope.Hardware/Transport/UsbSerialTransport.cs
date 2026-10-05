using System.IO.Ports;
using LogicScope.Core.Interfaces;
using LogicScope.Core.Models;
using LogicScope.Hardware.Protocol;

namespace LogicScope.Hardware.Transport;

/// <summary>
/// USB CDC serial transport. SerialPort is used only as the Windows COM device
/// handle; the baud parameter is conventional and does not rate-limit USB CDC.
/// </summary>
public sealed class UsbSerialTransport : ILogicScopeDevice
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SerialPort? _port;
    private TaskCompletionSource<DeviceStatus>? _startAcknowledgement;
    private volatile bool _captureActive;
    private volatile bool _finalPacketReceived;
    private int _disposed;

    public UsbSerialTransport(string portName) => PortName = portName;

    public string PortName { get; }
    public DeviceCapabilities? Capabilities { get; private set; }
    public bool IsConnected => _port?.IsOpen == true;
    public event EventHandler<DeviceStatus>? StatusReceived;

    public async Task<DeviceCapabilities> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsConnected && Capabilities is not null) return Capabilities;

        var port = new SerialPort(PortName, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = false,
            ReadTimeout = 1500,
            WriteTimeout = 1500,
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 64 * 1024
        };
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        _port = port;

        try
        {
            await WriteAsync(PacketCodec.EncodeGetCaps(), cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var reply = new byte[PacketCodec.CapsLength];
            await ReadExactlyAsync(port.BaseStream, reply, timeout.Token).ConfigureAwait(false);
            Capabilities = PacketCodec.DecodeCaps(reply);
            if (Capabilities.HardwareChannels != 16)
                throw new InvalidDataException($"Device reports {Capabilities.HardwareChannels} channels; expected 16.");
            if (Capabilities.MaxBurstRateHz == 0 || Capabilities.MaxStreamRateHz == 0 ||
                Capabilities.RamSamples == 0)
                throw new InvalidDataException("Device reports unavailable DMA/capture capabilities.");
            return Capabilities;
        }
        catch
        {
            port.Dispose();
            _port = null;
            Capabilities = null;
            throw;
        }
    }

    public async Task StartAsync(CaptureSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (!IsConnected) throw new InvalidOperationException("USB device is not connected.");
        var caps = Capabilities ?? throw new InvalidOperationException("GET_CAPS has not completed.");
        var maximum = settings.Mode == CaptureMode.Burst ? caps.MaxBurstRateHz : caps.MaxStreamRateHz;
        if (settings.SampleRateHz > maximum)
            throw new ArgumentOutOfRangeException(nameof(settings),
                $"Requested rate exceeds the device's configured ceiling ({maximum:N0} Hz).");
        var acknowledgement = new TaskCompletionSource<DeviceStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _startAcknowledgement, acknowledgement);
        _finalPacketReceived = false;
        _captureActive = true;
        try
        {
            await WriteAsync(PacketCodec.EncodeStart(settings), cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var status = await acknowledgement.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            var expectedState = settings.Mode == CaptureMode.Streaming ? (byte)1 : (byte)2;
            if (status.LastError != 0 || status.State != expectedState)
                throw new InvalidOperationException(
                    $"Device rejected START (state {status.State}, error {status.LastError}).");
        }
        catch
        {
            _captureActive = false;
            Interlocked.CompareExchange(ref _startAcknowledgement, null, acknowledgement);
            throw;
        }
    }

    public async Task<DeviceStatus> ToggleSelfTestAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("USB device is not connected.");
        if (_captureActive)
            throw new InvalidOperationException("Stop acquisition before toggling the self-test outputs.");
        await WriteAsync(PacketCodec.EncodeSelfTest(), cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var response = new byte[3];
        await ReadExactlyAsync(_port!.BaseStream, response, timeout.Token).ConfigureAwait(false);
        if (response[0] != PacketCodec.StatusMarker)
            throw new InvalidDataException("SELFTEST reply did not contain a status packet.");
        var status = new DeviceStatus(response[1], response[2]);
        if (status.LastError != 0 || status.State is not 0 and not 4)
            throw new InvalidOperationException(
                $"Device rejected SELFTEST (state {status.State}, error {status.LastError}).");
        StatusReceived?.Invoke(this, status);
        return status;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected || !_captureActive || _finalPacketReceived) return;
        await WriteAsync(PacketCodec.EncodeStop(), cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DataPacket> ReadDataAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var port = _port;
        if (port?.IsOpen != true) throw new InvalidOperationException("USB device is not connected.");

        await foreach (var wirePacket in PacketCodec.ReadPacketsAsync(port.BaseStream, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (wirePacket is DataWirePacket data)
            {
                if (data.Packet.IsFinal) _finalPacketReceived = true;
                yield return data.Packet;
                continue;
            }

            if (wirePacket is StatusWirePacket status)
            {
                StatusReceived?.Invoke(this, status.Status);
                Interlocked.Exchange(ref _startAcknowledgement, null)?.TrySetResult(status.Status);
                if ((status.Status.State is 0 or 4) && _captureActive)
                {
                    // State 4 is the idle self-test state. A capture can run
                    // while PWM is enabled and returns to state 4 on stop.
                    _captureActive = false;
                    yield break;
                }
            }
        }
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var port = _port;
        if (port?.IsOpen != true) throw new InvalidOperationException("USB device is not connected.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await port.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> target,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < target.Length)
        {
            var read = await stream.ReadAsync(target[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("COM port closed during GET_CAPS handshake.");
            offset += read;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UsbSerialTransport));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _captureActive = false;
            Interlocked.Exchange(ref _startAcknowledgement, null)?.TrySetException(
                new ObjectDisposedException(nameof(UsbSerialTransport)));
            _port?.Dispose();
            _port = null;
            _writeLock.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
