using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogicScope.App.ViewModels;
using LogicScope.Core.Acquisition;
using LogicScope.Core.Measurements;
using LogicScope.Core.Models;
using LogicScope.Decode;
using LogicScope.Export;
using LogicScope.Hardware.Discovery;
using LogicScope.Hardware.Transport;
using Microsoft.Win32;

namespace LogicScope.App;

public sealed record RateChoice(string Label, uint Hertz);
public sealed record ModeChoice(string Label, CaptureMode Mode);
public sealed record TriggerChannelChoice(string Label, byte Channel);
public sealed record EdgeChoice(string Label, TriggerEdge Edge);
public sealed record TriggerModeChoice(string Label);
public sealed record MeasurementRow(string Name, string Value);

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly RateChoice[] RateOptions =
    [
        new("100 kSa/s", 100_000), new("500 kSa/s", 500_000),
        new("1 MSa/s", 1_000_000), new("2 MSa/s", 2_000_000),
        new("5 MSa/s", 5_000_000), new("10 MSa/s", 10_000_000),
        new("20 MSa/s", 20_000_000)
    ];
    private readonly SampleRingBuffer _samples = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _durationCancellation;
    private UsbSerialTransport? _device;
    private AcquisitionEngine? _engine;
    private bool _initialized;
    private Task? _reconnectTask;
    private int _viewportWidth = 1200;
    private uint _sampleRateHz = 1_000_000;
    private long _softwareScanIndex;
    private bool _softwareTriggerFound;
    private bool _softwareTriggerEnabled;
    private CaptureMode _activeMode;
    private TriggerModeChoice _activeTriggerMode = new("Edge");
    private byte _activeTriggerChannel = 0xFF;
    private TriggerEdge _activeEdge = TriggerEdge.Rising;
    private ushort _activePatternMask;
    private ushort _activePatternValue;

    public MainViewModel()
    {
        RateChoices = RateOptions;
        ModeChoices = [new("Streaming", CaptureMode.Streaming), new("Burst", CaptureMode.Burst)];
        TriggerChannels = Enumerable.Range(0, 16)
            .Select(i => new TriggerChannelChoice($"D{i}", (byte)i))
            .Append(new TriggerChannelChoice("Disabled", 0xFF)).ToArray();
        EdgeChoices = [new("Rising", TriggerEdge.Rising), new("Falling", TriggerEdge.Falling)];
        TriggerModes = [new("Edge"), new("Pattern")];
        PreTriggerChoices = Enumerable.Range(0, 11).Select(i => (byte)(i * 10)).ToArray();
        DecoderNames = ["I2C", "SPI", "UART", "CAN 2.0 / FD raw", "1-Wire", "PS/2", "Manchester"];
        UartDataBitChoices = [5, 6, 7, 8, 9];
        UartParityChoices = Enum.GetValues<UartParity>();
        UartStopBitChoices = [1d, 1.5d, 2d];
        SpiWordBitChoices = Enumerable.Range(1, 16).ToArray();
        SelectedSpiChipSelectChannel = TriggerChannels[^1];
        Channels = new ObservableCollection<ChannelDisplayViewModel>(
            ChannelDefinition.DefaultChannels.Select(c => new ChannelDisplayViewModel(c)));
        DecodeResults = new ObservableCollection<DecoderResult>();
        MeasurementRows = new ObservableCollection<MeasurementRow>();
        Buses = new ObservableCollection<BusDefinition>
        {
            new("DATA", Enumerable.Range(0, 8).ToArray())
        };
        SelectedRate = RateChoices[2];
        SelectedMode = ModeChoices[0];
        SelectedTriggerChannel = TriggerChannels[^1];
        SelectedEdge = EdgeChoices[0];
        SelectedTriggerMode = TriggerModes[0];
        SelectedPreTriggerPercent = 20;
        PatternExpression = "A5";
        PatternMaskText = "00FF";
        StatusMessage = "Looking for a LogicScope device…";
        ConnectionText = "Disconnected";
        ThroughputText = "0.00 MSa/s  ·  0.00 MB/s";
        DropText = "0 dropped packets";
        CaptureInfoText = "No samples captured";
        CursorReadout = "Cursors: —";
        NewBusName = "DATA";
        NewBusChannels = "D0-D7";
    }

    public IReadOnlyList<RateChoice> RateChoices { get; }
    public IReadOnlyList<ModeChoice> ModeChoices { get; }
    public IReadOnlyList<TriggerChannelChoice> TriggerChannels { get; }
    public IReadOnlyList<EdgeChoice> EdgeChoices { get; }
    public IReadOnlyList<TriggerModeChoice> TriggerModes { get; }
    public IReadOnlyList<byte> PreTriggerChoices { get; }
    public IReadOnlyList<string> DecoderNames { get; }
    public IReadOnlyList<int> UartDataBitChoices { get; }
    public IReadOnlyList<UartParity> UartParityChoices { get; }
    public IReadOnlyList<double> UartStopBitChoices { get; }
    public IReadOnlyList<int> SpiWordBitChoices { get; }
    public ObservableCollection<ChannelDisplayViewModel> Channels { get; }
    public ObservableCollection<DecoderResult> DecodeResults { get; }
    public ObservableCollection<MeasurementRow> MeasurementRows { get; }
    public ObservableCollection<BusDefinition> Buses { get; }
    public SampleRingBuffer Samples => _samples;
    public long AvailableSamples => _samples.AvailableSamples;
    public long OldestSample => _samples.OldestIndex;
    public long NewestSampleExclusive => _samples.NextIndex;
    public double SamplesPerPixel { get; private set; } = 1;
    public long ViewStartSample { get; private set; }
    public long VisibleSampleCount => Math.Max(1, (long)Math.Ceiling(SamplesPerPixel * _viewportWidth));
    public uint CurrentSampleRateHz => _sampleRateHz;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureButtonText))]
    private bool isCapturing;

    [ObservableProperty] private bool isSelfTestActive;

    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string connectionText = string.Empty;
    [ObservableProperty] private string throughputText = string.Empty;
    [ObservableProperty] private string dropText = string.Empty;
    [ObservableProperty] private string captureInfoText = string.Empty;
    [ObservableProperty] private string cursorReadout = string.Empty;
    [ObservableProperty] private RateChoice selectedRate = RateOptions[2];
    [ObservableProperty] private ModeChoice selectedMode = new("Streaming", CaptureMode.Streaming);
    [ObservableProperty] private TriggerChannelChoice selectedTriggerChannel = new("Disabled", 0xFF);
    [ObservableProperty] private EdgeChoice selectedEdge = new("Rising", TriggerEdge.Rising);
    [ObservableProperty] private TriggerModeChoice selectedTriggerMode = new("Edge");
    [ObservableProperty] private string patternExpression = "A5";
    [ObservableProperty] private string patternMaskText = "00FF";
    [ObservableProperty] private byte selectedPreTriggerPercent = 20;
    [ObservableProperty] private int captureDurationSeconds;
    [ObservableProperty] private string selectedDecoderName = "I2C";
    [ObservableProperty] private int decoderPrimaryChannel;
    [ObservableProperty] private int decoderSecondaryChannel = 1;
    [ObservableProperty] private int decoderAuxiliaryChannel = 2;
    [ObservableProperty] private uint decoderBitRate = 100_000;
    [ObservableProperty] private int uartDataBits = 8;
    [ObservableProperty] private UartParity selectedUartParity = UartParity.None;
    [ObservableProperty] private double uartStopBits = 1;
    [ObservableProperty] private bool spiCpol;
    [ObservableProperty] private bool spiCpha;
    [ObservableProperty] private bool spiThreeWire;
    [ObservableProperty] private bool spiChipSelectActiveLow = true;
    [ObservableProperty] private TriggerChannelChoice selectedSpiChipSelectChannel = new("Disabled", 0xFF);
    [ObservableProperty] private int spiWordBits = 8;
    [ObservableProperty] private bool spiMsbFirst = true;
    [ObservableProperty] private bool manchesterRisingIsOne = true;
    [ObservableProperty] private long? cursorASample;
    [ObservableProperty] private long? cursorBSample;
    [ObservableProperty] private string newBusName = "DATA";
    [ObservableProperty] private string newBusChannels = "D0-D7";

    public string CaptureButtonText => IsCapturing ? "Stop capture" : "Start capture";

    public Task InitializeAsync()
    {
        if (_initialized) return Task.CompletedTask;
        _initialized = true;
        _reconnectTask = Task.Run(() => ReconnectLoopAsync(_lifetime.Token));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (await TryConnectAsync(_lifetime.Token).ConfigureAwait(true))
            StatusMessage = $"Connected on {_device?.PortName}.";
    }

    [RelayCommand]
    private async Task ToggleSelfTestAsync()
    {
        if (_engine?.IsRunning == true)
        {
            StatusMessage = "Stop acquisition before toggling the self-test outputs.";
            return;
        }
        if (_device?.IsConnected != true &&
            !await TryConnectAsync(_lifetime.Token).ConfigureAwait(true)) return;
        try
        {
            var status = await _device!.ToggleSelfTestAsync(_lifetime.Token).ConfigureAwait(true);
            IsSelfTestActive = status.State == 4;
            StatusMessage = IsSelfTestActive
                ? "1 MHz self-test outputs enabled on Teensy pins 22 and 23."
                : "Self-test outputs disabled.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Self-test command failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleCaptureAsync()
    {
        if (_engine?.IsRunning == true)
        {
            _durationCancellation?.Cancel();
            await _engine.StopAsync().ConfigureAwait(true);
            IsCapturing = false;
            StatusMessage = "Capture stopped.";
            return;
        }

        if (_device?.IsConnected != true || _engine is null)
        {
            StatusMessage = "Device is disconnected; reconnecting…";
            if (!await TryConnectAsync(_lifetime.Token).ConfigureAwait(true)) return;
        }

        var rate = SelectedRate.Hertz;
        var duration = CaptureDurationSeconds;
        if (duration is < 0 or > 86_400)
        {
            StatusMessage = "Capture duration must be 0 (continuous) or between 1 and 86,400 seconds.";
            return;
        }
        _activeMode = SelectedMode.Mode;
        _activeTriggerMode = SelectedTriggerMode;
        _activeTriggerChannel = SelectedTriggerChannel.Channel;
        _activeEdge = SelectedEdge.Edge;
        _softwareTriggerEnabled = _activeMode == CaptureMode.Streaming ||
                                  _activeTriggerMode.Label == "Pattern";
        _activePatternMask = 0;
        _activePatternValue = 0;
        if (_activeTriggerMode.Label == "Pattern")
        {
            try
            {
                var pattern = TriggerPattern.Parse(PatternExpression, PatternMaskText);
                _activePatternValue = pattern.Value;
                _activePatternMask = pattern.Mask;
            }
            catch (FormatException exception)
            {
                StatusMessage = $"Invalid trigger pattern: {exception.Message}";
                return;
            }
            if (_activePatternMask == 0)
            {
                StatusMessage = "Pattern trigger mask is empty; specify at least one 0/1/hex nibble.";
                return;
            }
        }
        CursorASample = null;
        CursorBSample = null;
        UpdateCursorReadout();
        DecodeResults.Clear();
        MeasurementRows.Clear();
        CaptureInfoText = "0 samples · 0 retained";
        ThroughputText = "0.00 MSa/s  ·  0.00 MB/s";
        DropText = "0 dropped packets";
        ViewStartSample = 0;
        SamplesPerPixel = 1;
        OnPropertyChanged(nameof(ViewStartSample));
        OnPropertyChanged(nameof(SamplesPerPixel));
        OnPropertyChanged(nameof(VisibleSampleCount));

        var firmwareTriggerChannel = SelectedMode.Mode == CaptureMode.Burst &&
                                     SelectedTriggerMode.Label == "Edge"
            ? SelectedTriggerChannel.Channel : (byte)0xFF;
        var settings = new CaptureSettings(rate, SelectedMode.Mode,
            firmwareTriggerChannel, SelectedEdge.Edge, SelectedPreTriggerPercent);
        try
        {
            _softwareScanIndex = 0;
            _softwareTriggerFound = false;
            await _engine!.StartAsync(settings, _lifetime.Token).ConfigureAwait(true);
            _sampleRateHz = DeriveActualRate(rate);
            OnPropertyChanged(nameof(CurrentSampleRateHz));
            IsCapturing = true;
            var runningMessage = _activeTriggerMode.Label == "Pattern" && _activeMode == CaptureMode.Burst
                ? $"Burst running at {_sampleRateHz:N0} Sa/s; pattern will be located host-side after capture (no firmware pre-trigger)."
                : $"{SelectedMode.Label} capture running at {_sampleRateHz:N0} Sa/s.";
            StatusMessage = IsSelfTestActive ? runningMessage + " Self-test outputs remain active." : runningMessage;
            _durationCancellation?.Cancel();
            _durationCancellation?.Dispose();
            _durationCancellation = new CancellationTokenSource();
            if (duration > 0)
                _ = StopAfterDurationAsync(duration, _durationCancellation.Token);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not start capture: {exception.Message}";
            IsCapturing = false;
        }
    }

    [RelayCommand]
    private async Task SaveCaptureAsync()
    {
        var dialog = new SaveFileDialog { Filter = "LogicScope capture (*.lgs)|*.lgs", DefaultExt = ".lgs" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var window = CurrentCaptureWindow();
            if (window.Samples.IsEmpty) { StatusMessage = "There are no samples to save."; return; }
            await LgsFile.SaveAsync(dialog.FileName, window, _lifetime.Token).ConfigureAwait(true);
            StatusMessage = $"Saved {window.Samples.Length:N0} samples.";
        }
        catch (Exception exception) { StatusMessage = $"Save failed: {exception.Message}"; }
    }

    [RelayCommand]
    private async Task OpenCaptureAsync()
    {
        var dialog = new OpenFileDialog { Filter = "LogicScope capture (*.lgs)|*.lgs" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (_engine?.IsRunning == true)
            {
                _durationCancellation?.Cancel();
                await _engine.StopAsync(_lifetime.Token).ConfigureAwait(true);
                IsCapturing = false;
            }
            var capture = await LgsFile.LoadAsync(dialog.FileName, _lifetime.Token).ConfigureAwait(true);
            _samples.Reset();
            _samples.Append(capture.Samples);
            _sampleRateHz = capture.SampleRateHz;
            OnPropertyChanged(nameof(CurrentSampleRateHz));
            OnSamplesChanged();
            CaptureInfoText = $"{capture.Samples.Length:N0} samples · {_samples.AvailableSamples:N0} retained";
            ThroughputText = "File capture · 0.00 MB/s";
            DropText = "0 dropped packets";
            ZoomToFit();
            StatusMessage = $"Opened {capture.Samples.Length:N0} samples at {capture.SampleRateHz:N0} Sa/s.";
        }
        catch (Exception exception) { StatusMessage = $"Open failed: {exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var dialog = new SaveFileDialog { Filter = "CSV samples (*.csv)|*.csv", DefaultExt = ".csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await CsvExport.WriteSamplesAsync(dialog.FileName, CurrentCaptureWindow(),
                ChannelDefinition.DefaultChannels, _lifetime.Token).ConfigureAwait(true);
            StatusMessage = "CSV export complete.";
        }
        catch (Exception exception) { StatusMessage = $"CSV export failed: {exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportVcdAsync()
    {
        var dialog = new SaveFileDialog { Filter = "Value Change Dump (*.vcd)|*.vcd", DefaultExt = ".vcd" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await Task.Run(() => VcdExport.Write(dialog.FileName, CurrentCaptureWindow()), _lifetime.Token)
                .ConfigureAwait(true);
            StatusMessage = "VCD export complete.";
        }
        catch (Exception exception) { StatusMessage = $"VCD export failed: {exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportSigrokAsync()
    {
        var dialog = new SaveFileDialog { Filter = "Sigrok / PulseView session (*.sr)|*.sr", DefaultExt = ".sr" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await SigrokSrExport.WriteAsync(dialog.FileName, CurrentCaptureWindow(),
                ChannelDefinition.DefaultChannels, _lifetime.Token).ConfigureAwait(true);
            StatusMessage = "Sigrok session export complete.";
        }
        catch (Exception exception) { StatusMessage = $"Sigrok export failed: {exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportSaleaeAsync()
    {
        var dialog = new SaveFileDialog { Filter = "Saleae Logic 2 raw binary (*.bin)|*.bin", DefaultExt = ".bin" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await SaleaeRawBinaryExport.WriteAsync(dialog.FileName, CurrentCaptureWindow(), _lifetime.Token)
                .ConfigureAwait(true);
            StatusMessage = "Raw binary export complete. Import it in Logic 2 using the matching 16-bit/sample-rate settings.";
        }
        catch (Exception exception) { StatusMessage = $"Binary export failed: {exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportDecoderCsvAsync()
    {
        var dialog = new SaveFileDialog { Filter = "Decoder results (*.csv)|*.csv", DefaultExt = ".csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await CsvExport.WriteDecoderResultsAsync(dialog.FileName, DecodeResults,
                _sampleRateHz, _lifetime.Token).ConfigureAwait(true);
            StatusMessage = "Decoder CSV export complete.";
        }
        catch (Exception exception) { StatusMessage = $"Decoder CSV export failed: {exception.Message}"; }
    }

    [RelayCommand]
    private async Task DecodeVisibleAsync()
    {
        var window = CurrentVisibleWindow();
        if (window.Samples.IsEmpty) { StatusMessage = "No visible samples to decode."; return; }
        try
        {
            var decoder = CreateDecoder();
            StatusMessage = $"Decoding {window.Samples.Length:N0} visible samples with {decoder.Name}…";
            var results = await Task.Run(() => decoder.Decode(window), _lifetime.Token).ConfigureAwait(true);
            DecodeResults.Clear();
            foreach (var result in results) DecodeResults.Add(result);
            StatusMessage = $"{results.Count:N0} {decoder.Name} results in visible window.";
        }
        catch (Exception exception) { StatusMessage = $"Decode failed: {exception.Message}"; }
    }

    [RelayCommand]
    private async Task AnalyzeMeasurementsAsync()
    {
        var window = CurrentVisibleWindow();
        if (window.Samples.IsEmpty) { StatusMessage = "No visible samples to measure."; return; }
        var selected = Channels.Where(channel => channel.IsVisible).Select(channel => channel.Index).ToArray();
        var measurements = await Task.Run(() => selected
            .Select(channel => DigitalMeasurements.AnalyzeChannel(window, channel)).ToArray(), _lifetime.Token)
            .ConfigureAwait(true);
        MeasurementRows.Clear();
        foreach (var item in measurements)
        {
            MeasurementRows.Add(new MeasurementRow($"D{item.Channel} frequency",
                item.FrequencyHz is { } f ? $"{f:G6} Hz" : "—"));
            MeasurementRows.Add(new MeasurementRow($"D{item.Channel} period / duty",
                item.PeriodSeconds is { } p ? $"{p:G6} s / {item.DutyCycle.GetValueOrDefault() * 100:G4}%" : "—"));
            MeasurementRows.Add(new MeasurementRow($"D{item.Channel} pulse min / max",
                item.MinPulseSeconds is { } min && item.MaxPulseSeconds is { } max
                    ? $"{min:G5} s / {max:G5} s" : "—"));
            MeasurementRows.Add(new MeasurementRow($"D{item.Channel} estimated rise time",
                item.EstimatedRiseTimeSeconds is { } rise ? $"{rise:G5} s (sample-quantized)" : "—"));
            if (selected.Length > 0 && item.Channel != selected[0])
            {
                var skew = DigitalMeasurements.ChannelSkewSeconds(window, selected[0], item.Channel);
                MeasurementRows.Add(new MeasurementRow($"D{item.Channel} skew vs D{selected[0]}",
                    skew is { } value ? $"{value:G5} s" : "—"));
            }
        }
        StatusMessage = $"Measurements calculated for {selected.Length} visible channels.";
    }

    [RelayCommand]
    private void ZoomToFit()
    {
        var count = _samples.AvailableSamples;
        if (count == 0) { ViewStartSample = 0; SamplesPerPixel = 1; }
        else
        {
            ViewStartSample = _samples.OldestIndex;
            SamplesPerPixel = Math.Max(1d / 16, count / (double)Math.Max(1, _viewportWidth));
        }
        OnPropertyChanged(nameof(ViewStartSample));
        OnPropertyChanged(nameof(SamplesPerPixel));
        OnPropertyChanged(nameof(VisibleSampleCount));
        StatusMessage = "Zoomed to retained capture.";
    }

    [RelayCommand]
    private void AddBus()
    {
        try
        {
            var channels = ParseBusChannels(NewBusChannels);
            if (channels.Count == 0) throw new FormatException("Select at least one channel.");
            var name = string.IsNullOrWhiteSpace(NewBusName) ? "BUS" : NewBusName.Trim();
            var existing = Buses.FirstOrDefault(bus => bus.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) Buses.Remove(existing);
            Buses.Add(new BusDefinition(name, channels));
            StatusMessage = $"Bus {name} added ({channels.Count} channels).";
        }
        catch (Exception exception) { StatusMessage = $"Bus definition: {exception.Message}"; }
    }

    public void SetViewportWidth(int pixelWidth)
    {
        if (pixelWidth <= 0) return;
        _viewportWidth = pixelWidth;
        OnPropertyChanged(nameof(VisibleSampleCount));
    }

    public void Zoom(double factor, double centerFraction)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        centerFraction = Math.Clamp(centerFraction, 0, 1);
        var centerSample = ViewStartSample + centerFraction * _viewportWidth * SamplesPerPixel;
        SamplesPerPixel = Math.Clamp(SamplesPerPixel * factor, 1d / 16,
            Math.Max(1, _samples.Capacity * 4d));
        ViewStartSample = (long)Math.Round(centerSample - centerFraction * _viewportWidth * SamplesPerPixel);
        ClampView();
        OnPropertyChanged(nameof(SamplesPerPixel));
        OnPropertyChanged(nameof(ViewStartSample));
        OnPropertyChanged(nameof(VisibleSampleCount));
    }

    public void PanPixels(double deltaPixels)
    {
        ViewStartSample -= (long)Math.Round(deltaPixels * SamplesPerPixel);
        ClampView();
        OnPropertyChanged(nameof(ViewStartSample));
    }

    public void PlaceCursor(long sampleIndex)
    {
        if (CursorASample is null || CursorBSample is not null)
        {
            CursorASample = sampleIndex;
            CursorBSample = null;
        }
        else CursorBSample = sampleIndex;
        UpdateCursorReadout();
    }

    public void JumpToFrame(int oneBasedFrame)
    {
        if (oneBasedFrame < 1 || oneBasedFrame > DecodeResults.Count) return;
        var result = DecodeResults[oneBasedFrame - 1];
        ViewStartSample = (long)(result.StartSample - VisibleSampleCount / 2d);
        ClampView();
        OnPropertyChanged(nameof(ViewStartSample));
        StatusMessage = $"Jumped to frame {oneBasedFrame}: {result.Label}";
    }

    private ProtocolDecoder CreateDecoder() => SelectedDecoderName switch
    {
        "I2C" => new I2cDecoder(DecoderPrimaryChannel, DecoderSecondaryChannel),
        "SPI" => new SpiDecoder(DecoderPrimaryChannel, DecoderSecondaryChannel,
            DecoderAuxiliaryChannel,
            chipSelectChannel: SelectedSpiChipSelectChannel.Channel == 0xFF
                ? null : SelectedSpiChipSelectChannel.Channel,
            cpol: SpiCpol, cpha: SpiCpha, wordBits: SpiWordBits,
            threeWire: SpiThreeWire, chipSelectActiveLow: SpiChipSelectActiveLow,
            msbFirst: SpiMsbFirst),
        "UART" => new UartDecoder(DecoderPrimaryChannel, DecoderBitRate, UartDataBits,
            SelectedUartParity, UartStopBits),
        "CAN 2.0 / FD raw" => new CanDecoder(DecoderPrimaryChannel, DecoderBitRate),
        "1-Wire" => new OneWireDecoder(DecoderPrimaryChannel),
        "PS/2" => new Ps2Decoder(DecoderPrimaryChannel, DecoderSecondaryChannel),
        "Manchester" => new ManchesterDecoder(DecoderPrimaryChannel, DecoderBitRate,
            ManchesterRisingIsOne),
        _ => throw new InvalidOperationException("Unknown decoder selection.")
    };

    private SampleWindow CurrentCaptureWindow() =>
        _samples.ReadWindow(_samples.OldestIndex, _samples.AvailableSamples, _sampleRateHz);

    private SampleWindow CurrentVisibleWindow() =>
        _samples.ReadWindow(ViewStartSample, VisibleSampleCount, _sampleRateHz);

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_device?.IsConnected == true && _engine is not null) return true;
            if (_engine is not null)
            {
                var previousEngine = _engine;
                _engine = null;
                _device = null;
                try { await previousEngine.DisposeAsync().ConfigureAwait(false); }
                catch { /* A disconnected COM handle may fail while being stopped. */ }
            }
            await OnUiAsync(() =>
            {
                ConnectionText = "Searching…";
                StatusMessage = "Probing Windows CDC COM ports with GET_CAPS…";
            }).ConfigureAwait(false);
            var progress = new Progress<string>(message => _ = OnUiAsync(() => StatusMessage = message));
            var (device, caps) = await DeviceDiscovery.FindFirstAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            if (device is null || caps is null)
            {
                await OnUiAsync(() => ConnectionText = "Disconnected").ConfigureAwait(false);
                return false;
            }

            _device = device;
            _device.StatusReceived += OnDeviceStatus;
            _engine = new AcquisitionEngine(device, _samples);
            _engine.MetricsUpdated += OnMetricsUpdated;
            _engine.CaptureFaulted += OnCaptureFaulted;
            await OnUiAsync(() =>
            {
                ConnectionText = $"Connected · {device.PortName} · FW {caps.FirmwareVersion}";
                StatusMessage = $"16 channels ready; {caps.RamSamples:N0} burst samples advertised.";
            }).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            await OnUiAsync(() =>
            {
                ConnectionText = "Disconnected";
                StatusMessage = $"Device discovery failed: {exception.Message}";
            }).ConfigureAwait(false);
            return false;
        }
        finally { _connectLock.Release(); }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_device?.IsConnected != true)
                await TryConnectAsync(cancellationToken).ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task StopAfterDurationAsync(int durationSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds), cancellationToken).ConfigureAwait(false);
            if (_engine?.IsRunning == true) await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private void OnMetricsUpdated(object? sender, CaptureMetrics metrics)
    {
        CheckSoftwareTrigger();
        _ = OnUiAsync(() =>
        {
            IsCapturing = metrics.IsRunning;
            if (metrics.SelfTestActive) IsSelfTestActive = true;
            ThroughputText = $"{metrics.SamplesPerSecond / 1_000_000d:F2} MSa/s  ·  {metrics.MegabytesPerSecond:F2} MB/s";
            DropText = metrics.DmaOverrun
                ? $"{metrics.DroppedPackets:N0} dropped packets · DMA overrun"
                : $"{metrics.DroppedPackets:N0} dropped packets";
            CaptureInfoText = $"{metrics.SamplesReceived:N0} samples · {_samples.AvailableSamples:N0} retained";
            if (!metrics.IsRunning && metrics.Triggered && _activeMode == CaptureMode.Burst)
                StatusMessage = "Firmware edge trigger fired; burst upload complete.";
            if (metrics.DmaOverrun)
                StatusMessage = "DMA ring overrun: capture is incomplete.";
            OnSamplesChanged();
        });
    }

    private void CheckSoftwareTrigger()
    {
        if (!_softwareTriggerEnabled || _softwareTriggerFound) return;
        var end = _samples.NextIndex;
        long? match = _activeTriggerMode.Label == "Pattern"
            ? _samples.FindPattern(new TriggerPattern(_activePatternValue, _activePatternMask),
                _softwareScanIndex, end)
            : _activeTriggerChannel == 0xFF ? null
                : _samples.FindEdge(_activeTriggerChannel, _activeEdge, _softwareScanIndex, end);
        _softwareScanIndex = end;
        if (match is not { } triggerIndex) return;

        _softwareTriggerFound = true;
        _ = OnUiAsync(() =>
        {
            CursorASample = triggerIndex;
            CursorBSample = null;
            UpdateCursorReadout();
            ViewStartSample = Math.Max(_samples.OldestIndex,
                triggerIndex - VisibleSampleCount / 4);
            OnPropertyChanged(nameof(ViewStartSample));
            StatusMessage = _activeTriggerMode.Label == "Pattern"
                ? $"Host pattern trigger matched at sample {triggerIndex:N0}."
                : $"Host edge trigger matched at sample {triggerIndex:N0}.";
        });
    }

    private void OnDeviceStatus(object? sender, DeviceStatus status)
    {
        _ = OnUiAsync(() =>
        {
            if (status.State == 4) IsSelfTestActive = true;
            else if (status.State == 0) IsSelfTestActive = false;
            if ((status.State is 0 or 4) && !(_engine?.IsRunning ?? false)) IsCapturing = false;
            if (status.LastError != 0)
                StatusMessage = $"Device status {status.State}, error {status.LastError}.";
        });
    }

    private void OnCaptureFaulted(Exception exception)
    {
        _ = OnUiAsync(() =>
        {
            IsCapturing = false;
            ConnectionText = "Connection lost";
            StatusMessage = $"USB acquisition stopped: {exception.Message}. Reconnecting…";
        });
        _ = DisconnectAfterFaultAsync();
    }

    private async Task DisconnectAfterFaultAsync()
    {
        var engine = _engine;
        _engine = null;
        _device = null;
        if (engine is not null)
        {
            try { await engine.DisposeAsync().ConfigureAwait(false); }
            catch { /* The transport already failed; the reconnect loop owns recovery. */ }
        }
    }

    private async Task OnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return; }
        await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
    }

    private void OnSamplesChanged()
    {
        OnPropertyChanged(nameof(AvailableSamples));
        OnPropertyChanged(nameof(OldestSample));
        OnPropertyChanged(nameof(NewestSampleExclusive));
        OnPropertyChanged(nameof(VisibleSampleCount));
        if (_samples.AvailableSamples > 0 && ViewStartSample == 0 && _samples.OldestIndex == 0)
            ViewStartSample = Math.Max(0, _samples.NextIndex - VisibleSampleCount);
        OnPropertyChanged(nameof(ViewStartSample));
    }

    private void ClampView()
    {
        var oldest = _samples.OldestIndex;
        var latestStart = Math.Max(oldest, _samples.NextIndex - VisibleSampleCount);
        ViewStartSample = Math.Clamp(ViewStartSample, oldest, latestStart);
    }

    private void UpdateCursorReadout()
    {
        if (CursorASample is { } a && CursorBSample is { } b)
        {
            var readout = $"Δt {(b - a) / (double)_sampleRateHz:G7} s  ·  Δsamples {b - a:N0}";
            if (_samples.TryGetSample(a, out var sampleA) && _samples.TryGetSample(b, out var sampleB))
            {
                var levels = Channels.Where(channel => channel.IsVisible)
                    .Select(channel => $"D{channel.Index}:{((sampleA >> channel.Index) & 1)}→{((sampleB >> channel.Index) & 1)}");
                readout += "  ·  " + string.Join("  ", levels);
            }
            CursorReadout = readout;
        }
        else
            CursorReadout = CursorASample is { } one
                ? $"Cursor A {one / (double)_sampleRateHz:G7} s · click again for B"
                : "Cursors: Shift-click waveform to place A and B";
    }

    private static uint DeriveActualRate(uint requestedRate)
    {
        const uint ipgHz = 150_000_000;
        if (requestedRate == 0) return 0;
        var divider = Math.Max(1u, (ipgHz + requestedRate / 2u) / requestedRate);
        return ipgHz / divider;
    }

    private static IReadOnlyList<int> ParseBusChannels(string text)
    {
        var result = new SortedSet<int>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 1) result.Add(Validate(ParseChannel(range[0])));
            else if (range.Length == 2)
            {
                var first = ParseChannel(range[0]);
                var last = ParseChannel(range[1]);
                if (first > last) (first, last) = (last, first);
                for (var channel = first; channel <= last; channel++) result.Add(Validate(channel));
            }
            else throw new FormatException($"Invalid channel selector '{part}'. Use D0-D7 or D0,D2,D5.");
        }
        return result.ToArray();

        static int ParseChannel(string token)
        {
            token = token.Trim();
            if (token.StartsWith('D') || token.StartsWith('d')) token = token[1..];
            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : throw new FormatException($"Invalid channel selector '{token}'.");
        }

        static int Validate(int channel) => channel is >= 0 and < 16
            ? channel : throw new FormatException("Channel indexes range from D0 through D15.");
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _durationCancellation?.Cancel();
        if (_reconnectTask is not null)
        {
            try { await _reconnectTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            try { await engine.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
        _durationCancellation?.Dispose();
        _connectLock.Dispose();
        _lifetime.Dispose();
    }
}
