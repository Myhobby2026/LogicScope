using System.Diagnostics;
using System.Threading.Channels;
using LogicScope.Core.Interfaces;
using LogicScope.Core.Models;

namespace LogicScope.Core.Acquisition;

/// <summary>
/// Separates byte acquisition from UI/sample-store work. A bounded Channel
/// provides backpressure instead of unbounded allocation when the renderer or
/// disk writer is busy; the fixed ring remains exactly 16 MiB by default.
/// </summary>
public sealed class AcquisitionEngine : IAsyncDisposable
{
    private readonly ILogicScopeDevice _device;
    private Channel<DataPacket> _blocks = CreateBlockChannel();
    private readonly object _stateGate = new();
    private CancellationTokenSource? _runCts;
    private Task? _pumpTask;
    private Task? _consumerTask;
    private long _samplesReceived;
    private long _droppedPackets;
    private int _triggered;
    private int _dmaOverrun;
    private int _selfTestActive;
    private double _samplesPerSecond;
    private double _megabytesPerSecond;
    private int _isRunning;

    public AcquisitionEngine(ILogicScopeDevice device, SampleRingBuffer? ring = null)
    {
        _device = device;
        Samples = ring ?? new SampleRingBuffer();
    }

    public SampleRingBuffer Samples { get; }
    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;
    public long SamplesReceived => Interlocked.Read(ref _samplesReceived);
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);
    public bool Triggered => Volatile.Read(ref _triggered) != 0;
    public bool DmaOverrun => Volatile.Read(ref _dmaOverrun) != 0;
    public bool SelfTestActive => Volatile.Read(ref _selfTestActive) != 0;
    public double SamplesPerSecond => Volatile.Read(ref _samplesPerSecond);
    public double MegabytesPerSecond => Volatile.Read(ref _megabytesPerSecond);
    public event EventHandler<CaptureMetrics>? MetricsUpdated;
    public event Action<Exception>? CaptureFaulted;

    public async Task StartAsync(CaptureSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        lock (_stateGate)
        {
            if (IsRunning) throw new InvalidOperationException("Capture is already active.");
            if (_pumpTask is { IsCompleted: false })
                throw new InvalidOperationException("Previous capture is still shutting down.");
            Samples.Reset();
            Interlocked.Exchange(ref _samplesReceived, 0);
            Interlocked.Exchange(ref _droppedPackets, 0);
            Volatile.Write(ref _triggered, 0);
            Volatile.Write(ref _dmaOverrun, 0);
            Volatile.Write(ref _selfTestActive, 0);
            Volatile.Write(ref _samplesPerSecond, 0);
            Volatile.Write(ref _megabytesPerSecond, 0);
            _runCts?.Dispose();
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var runCts = _runCts;
            _blocks = CreateBlockChannel();
            var runChannel = _blocks;
            Volatile.Write(ref _isRunning, 1);
            _consumerTask = Task.Run(() => ConsumeAsync(runChannel, runCts.Token), CancellationToken.None);
            // Start the reader before START is sent so an immediate short burst
            // cannot arrive before a USB read is posted.
            _pumpTask = Task.Run(() => PumpAsync(runChannel, runCts.Token), CancellationToken.None);
        }

        try
        {
            await _device.StartAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _runCts?.Cancel();
            if (_pumpTask is not null)
            {
                try { await _pumpTask.ConfigureAwait(false); }
                catch { }
            }
            if (_consumerTask is not null)
            {
                try { await _consumerTask.ConfigureAwait(false); }
                catch { }
            }
            Volatile.Write(ref _isRunning, 0);
            throw;
        }
        PublishMetrics();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? pump;
        Task? consumer;
        lock (_stateGate)
        {
            if (!IsRunning) return;
            pump = _pumpTask;
            consumer = _consumerTask;
        }

        var pumpStoppedGracefully = false;
        try
        {
            await _device.StopAsync(cancellationToken).ConfigureAwait(false);
            if (pump is not null)
                await pump.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            pumpStoppedGracefully = true;
        }
        catch (TimeoutException)
        {
            _runCts?.Cancel();
        }
        finally
        {
            if (!pumpStoppedGracefully)
            {
                _runCts?.Cancel();
                if (pump is not null)
                {
                    try { await pump.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
            if (consumer is not null)
            {
                try { await consumer.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            Volatile.Write(ref _isRunning, 0);
            PublishMetrics();
        }
    }

    private async Task PumpAsync(Channel<DataPacket> channel, CancellationToken cancellationToken)
    {
        byte? expectedSequence = null;
        try
        {
            await foreach (var packet in _device.ReadDataAsync(cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (expectedSequence is { } expected && packet.Sequence != expected)
                {
                    var missed = (byte)(packet.Sequence - expected);
                    Interlocked.Add(ref _droppedPackets, missed);
                }
                expectedSequence = unchecked((byte)(packet.Sequence + 1));
                await channel.Writer.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CaptureFaulted?.Invoke(exception);
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private async Task ConsumeAsync(Channel<DataPacket> channel, CancellationToken cancellationToken)
    {
        var reportClock = Stopwatch.StartNew();
        long lastSamples = 0;
        try
        {
            await foreach (var packet in channel.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (packet.Triggered) Interlocked.Exchange(ref _triggered, 1);
                if (packet.DmaOverrun) Interlocked.Exchange(ref _dmaOverrun, 1);
                if (packet.SelfTestActive) Interlocked.Exchange(ref _selfTestActive, 1);
                if (packet.Samples.Length != 0)
                {
                    Samples.Append(packet.Samples);
                    Interlocked.Add(ref _samplesReceived, packet.Samples.Length);
                }

                if (reportClock.ElapsedMilliseconds >= 200)
                {
                    var nowSamples = SamplesReceived;
                    var elapsed = reportClock.Elapsed.TotalSeconds;
                    if (elapsed > 0)
                    {
                        Volatile.Write(ref _samplesPerSecond, (nowSamples - lastSamples) / elapsed);
                        Volatile.Write(ref _megabytesPerSecond,
                            (nowSamples - lastSamples) * sizeof(ushort) / elapsed / (1024d * 1024d));
                    }
                    lastSamples = nowSamples;
                    reportClock.Restart();
                    PublishMetrics();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            PublishMetrics();
        }
    }

    private static Channel<DataPacket> CreateBlockChannel() => Channel.CreateBounded<DataPacket>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

    private void PublishMetrics() => MetricsUpdated?.Invoke(this,
        new CaptureMetrics(SamplesReceived, DroppedPackets, SamplesPerSecond,
            MegabytesPerSecond, IsRunning, Triggered, DmaOverrun, SelfTestActive));

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            _runCts?.Dispose();
            await _device.DisposeAsync().ConfigureAwait(false);
        }
    }
}
