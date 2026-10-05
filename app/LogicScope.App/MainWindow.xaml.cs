using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LogicScope.App.ViewModels;
using LogicScope.Core.Acquisition;
using LogicScope.Core.Models;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace LogicScope.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly DispatcherTimer _renderTimer;
    private bool _dragging;
    private double _lastDragX;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        WaveformSurface.MouseWheel += WaveformSurface_MouseWheel;
        WaveformSurface.MouseLeftButtonDown += WaveformSurface_MouseLeftButtonDown;
        WaveformSurface.MouseLeftButtonUp += WaveformSurface_MouseLeftButtonUp;
        WaveformSurface.MouseMove += WaveformSurface_MouseMove;
        WaveformSurface.SizeChanged += WaveformSurface_SizeChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += (_, _) => WaveformSurface.InvalidateVisual();
        _renderTimer.Start();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _renderTimer.Stop();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        await _viewModel.DisposeAsync();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
        var number = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => 0
        };
        if (number > 0 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _viewModel.JumpToFrame(number);
            e.Handled = true;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ViewStartSample) or
            nameof(MainViewModel.SamplesPerPixel) or nameof(MainViewModel.VisibleSampleCount) or
            nameof(MainViewModel.AvailableSamples) or nameof(MainViewModel.CursorASample) or
            nameof(MainViewModel.CursorBSample))
            WaveformSurface.InvalidateVisual();
    }

    private void WaveformSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1d;
        _viewModel.SetViewportWidth(Math.Max(1, (int)Math.Round(e.NewSize.Width * scale)));
    }

    private void WaveformSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var point = e.GetPosition(WaveformSurface);
        var fraction = WaveformSurface.ActualWidth <= 0 ? 0.5 : point.X / WaveformSurface.ActualWidth;
        _viewModel.Zoom(e.Delta > 0 ? 0.8 : 1.25, fraction);
        e.Handled = true;
    }

    private void WaveformSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WaveformSurface.Focus();
        var point = e.GetPosition(WaveformSurface);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var sample = _viewModel.ViewStartSample +
                         (long)Math.Round(point.X * _viewModel.SamplesPerPixel);
            _viewModel.PlaceCursor(sample);
            e.Handled = true;
            return;
        }
        _dragging = true;
        _lastDragX = point.X;
        WaveformSurface.CaptureMouse();
        e.Handled = true;
    }

    private void WaveformSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        WaveformSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void WaveformSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetPosition(WaveformSurface);
        var delta = point.X - _lastDragX;
        _lastDragX = point.X;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1d;
        _viewModel.PanPixels(delta * scale);
    }

    private void WaveformSurface_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;
        _viewModel.SetViewportWidth(Math.Max(1, info.Width));
        DrawWaveforms(canvas, info.Width, info.Height, _viewModel);
    }

    private static void DrawWaveforms(SKCanvas canvas, int width, int height, MainViewModel vm)
    {
        canvas.Clear(new SKColor(10, 16, 24));
        using var gridPaint = new SKPaint { Color = new SKColor(41, 55, 73), StrokeWidth = 1, IsAntialias = false };
        using var minorPaint = new SKPaint { Color = new SKColor(25, 37, 51), StrokeWidth = 1, IsAntialias = false };
        using var textPaint = new SKPaint { Color = new SKColor(121, 140, 163), TextSize = 11, IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Segoe UI") };
        using var annotationPaint = new SKPaint { Color = new SKColor(238, 174, 92), StrokeWidth = 1, IsAntialias = true };

        const float rulerHeight = 26;
        const float channelTop = 42;
        var busRows = Math.Min(3, vm.Buses.Count);
        var available = Math.Max(180, height - channelTop - 16 - busRows * 25);
        var laneHeight = Math.Clamp(available / 16f, 13f, 23f);
        var laneBottom = channelTop + laneHeight * 16;

        canvas.DrawLine(0, rulerHeight, width, rulerHeight, gridPaint);
        var gridSpacing = SelectGridSpacing(vm.SamplesPerPixel / vm.CurrentSampleRateHz * 100d);
        var visibleStartSeconds = vm.ViewStartSample / (double)Math.Max(1, vm.CurrentSampleRateHz);
        var firstTick = Math.Ceiling(visibleStartSeconds / gridSpacing) * gridSpacing;
        for (var time = firstTick; ; time += gridSpacing)
        {
            var x = (float)((time * vm.CurrentSampleRateHz - vm.ViewStartSample) / vm.SamplesPerPixel);
            if (x > width) break;
            if (x < 0) continue;
            canvas.DrawLine(x, rulerHeight, x, height, gridPaint);
            canvas.DrawText(FormatTime(time), x + 4, 16, textPaint);
        }

        for (var x = 0; x < width; x += 20)
            canvas.DrawLine(x, channelTop, x, laneBottom, minorPaint);

        for (var channel = 0; channel < 16; channel++)
        {
            var top = channelTop + laneHeight * channel;
            canvas.DrawLine(0, top + laneHeight - 1, width, top + laneHeight - 1, minorPaint);
        }

        var availableSamples = vm.Samples.AvailableSamples;
        if (availableSamples == 0)
        {
            using var emptyPaint = new SKPaint { Color = new SKColor(108, 126, 149), TextSize = 15, IsAntialias = true };
            canvas.DrawText("Waiting for samples — connect a LogicScope device or open a .lgs capture.",
                24, channelTop + 36, emptyPaint);
            DrawBusLabels(canvas, vm, channelTop, laneHeight, width, textPaint);
            return;
        }

        SampleEnvelope[] envelope;
        try
        {
            envelope = vm.Samples.AggregateVisible(vm.ViewStartSample,
                vm.VisibleSampleCount, width);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        for (var channel = 0; channel < 16; channel++)
        {
            if (!vm.Channels[channel].IsVisible) continue;
            var color = ToSkColor(vm.Channels[channel].Color);
            using var signalPaint = new SKPaint { Color = color, StrokeWidth = 1.5f, IsAntialias = false };
            var top = channelTop + laneHeight * channel;
            var highY = top + laneHeight * 0.28f;
            var lowY = top + laneHeight * 0.80f;
            var mask = (ushort)(1 << channel);
            var priorLevel = false;
            var havePrior = false;

            for (var x = 0; x < envelope.Length; x++)
            {
                var pixel = envelope[x];
                if (!pixel.HasSamples) continue;
                var firstHigh = (pixel.First & mask) != 0;
                var lastHigh = (pixel.Last & mask) != 0;
                var anyHigh = (pixel.AnyHigh & mask) != 0;
                var anyLow = (pixel.AnyLow & mask) != 0;
                if (havePrior && priorLevel != firstHigh)
                    canvas.DrawLine(x, highY, x, lowY, signalPaint);
                if (anyHigh && anyLow)
                    canvas.DrawLine(x + 0.5f, highY, x + 0.5f, lowY, signalPaint);
                var y = firstHigh ? highY : lowY;
                canvas.DrawLine(x, y, x + 1, y, signalPaint);
                priorLevel = lastHigh;
                havePrior = true;
            }
        }

        DrawBusValues(canvas, vm, envelope, channelTop, laneHeight, width, textPaint);
        DrawCursors(canvas, vm, width, laneBottom, height);
        DrawDecodeAnnotations(canvas, vm, width, channelTop, annotationPaint, textPaint);
    }

    private static void DrawBusLabels(SKCanvas canvas, MainViewModel vm, float channelTop,
        float laneHeight, int width, SKPaint paint)
    {
        var y = channelTop + laneHeight * 16 + 18;
        foreach (var bus in vm.Buses.Take(3))
        {
            canvas.DrawText(bus.Name, 10, y, paint);
            y += 23;
        }
    }

    private static void DrawBusValues(SKCanvas canvas, MainViewModel vm, SampleEnvelope[] envelope,
        float channelTop, float laneHeight, int width, SKPaint textPaint)
    {
        var busIndex = 0;
        using var busBack = new SKPaint { Color = new SKColor(19, 30, 43), IsAntialias = true };
        using var separatorPaint = new SKPaint { Color = new SKColor(29, 42, 58), StrokeWidth = 1 };
        using var busText = new SKPaint { Color = new SKColor(173, 192, 215), TextSize = 10,
            Typeface = SKTypeface.FromFamilyName("Consolas"), IsAntialias = true };
        foreach (var bus in vm.Buses.Take(3))
        {
            var y = channelTop + laneHeight * 16 + 3 + busIndex * 24;
            canvas.DrawLine(0, y + 21, width, y + 21, separatorPaint);
            canvas.DrawText(bus.Name, 8, y + 15, textPaint);
            int? previous = null;
            var lastLabelX = -100f;
            for (var x = 0; x < envelope.Length; x++)
            {
                if (!envelope[x].HasSamples) continue;
                var value = bus.ReadValue(envelope[x].First);
                if (previous == value || x - lastLabelX < 48) { previous = value; continue; }
                var label = $"0x{value:X}";
                var labelWidth = busText.MeasureText(label) + 12;
                var labelX = Math.Min(width - labelWidth - 3, x + 2);
                if (labelX < 58) labelX = 58;
                var rect = new SKRect(labelX, y + 2, labelX + labelWidth, y + 20);
                canvas.DrawRoundRect(rect, 4, 4, busBack);
                canvas.DrawText(label, labelX + 6, y + 15, busText);
                lastLabelX = labelX;
                previous = value;
            }
            busIndex++;
        }
    }

    private static void DrawCursors(SKCanvas canvas, MainViewModel vm, int width,
        float laneBottom, int height)
    {
        using var paint = new SKPaint { Color = new SKColor(244, 205, 107), StrokeWidth = 1,
            IsAntialias = true, PathEffect = SKPathEffect.CreateDash([4, 3], 0) };
        DrawCursor(vm.CursorASample, "A");
        DrawCursor(vm.CursorBSample, "B");

        void DrawCursor(long? sample, string label)
        {
            if (sample is not { } value) return;
            var x = (float)((value - vm.ViewStartSample) / vm.SamplesPerPixel);
            if (x < 0 || x > width) return;
            canvas.DrawLine(x, 26, x, height, paint);
            using var text = new SKPaint { Color = paint.Color, TextSize = 10, IsAntialias = true };
            canvas.DrawText(label, x + 3, Math.Max(38, laneBottom - 5), text);
        }
    }

    private static void DrawDecodeAnnotations(SKCanvas canvas, MainViewModel vm, int width,
        float channelTop, SKPaint markerPaint, SKPaint textPaint)
    {
        var start = vm.ViewStartSample;
        var end = start + vm.VisibleSampleCount;
        var visibleResults = vm.DecodeResults.Where(result => result.StartSample >= start && result.StartSample < end)
            .Take(300);
        foreach (var result in visibleResults)
        {
            var x = (float)((result.StartSample - start) / vm.SamplesPerPixel);
            if (x < 0 || x >= width) continue;
            markerPaint.Color = result.IsError ? new SKColor(255, 98, 107) : new SKColor(241, 181, 89);
            canvas.DrawLine(x, channelTop - 12, x, channelTop + 2, markerPaint);
            canvas.DrawCircle(x, channelTop - 12, 3, markerPaint);
            if (x < width - 90)
            {
                textPaint.Color = markerPaint.Color;
                canvas.DrawText(Truncate(result.Label, 22), x + 4, channelTop - 4, textPaint);
            }
        }
    }

    private static float SelectGridSpacing(double desired)
    {
        if (!double.IsFinite(desired) || desired <= 0) return 1e-6f;
        var exponent = Math.Floor(Math.Log10(desired));
        var scale = Math.Pow(10, exponent);
        var normalized = desired / scale;
        var factor = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return (float)(factor * scale);
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 1e-6) return $"{seconds * 1e9:0.##} ns";
        if (seconds < 1e-3) return $"{seconds * 1e6:0.##} µs";
        if (seconds < 1) return $"{seconds * 1e3:0.##} ms";
        return $"{seconds:0.###} s";
    }

    private static SKColor ToSkColor(System.Windows.Media.Brush brush)
    {
        if (brush is not System.Windows.Media.SolidColorBrush solid) return new SKColor(67, 199, 232);
        var color = solid.Color;
        return new SKColor(color.R, color.G, color.B, color.A);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
