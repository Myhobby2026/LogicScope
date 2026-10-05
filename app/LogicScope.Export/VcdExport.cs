using System.Globalization;
using LogicScope.Core.Models;

namespace LogicScope.Export;

public static class VcdExport
{
    public static void Write(string path, SampleWindow window,
        IReadOnlyList<ChannelDefinition>? channels = null)
    {
        if (window.SampleRateHz == 0) throw new ArgumentOutOfRangeException(nameof(window));
        channels ??= ChannelDefinition.DefaultChannels;
        var ordered = channels.OrderBy(c => c.Index).ToArray();
        if (ordered.Length != 16 || ordered.Select(c => c.Index).Distinct().Count() != 16 ||
            ordered.Any(c => (uint)c.Index >= 16))
            throw new ArgumentException("VCD export needs 16 uniquely indexed channels.");
        var samples = window.Samples.Span;
        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        writer.WriteLine("$date");
        writer.WriteLine(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        writer.WriteLine("$end");
        writer.WriteLine("$version LogicScope $end");
        writer.WriteLine("$timescale 1 ns $end");
        writer.WriteLine("$scope module logic $end");
        foreach (var channel in ordered)
            writer.WriteLine($"$var wire 1 c{channel.Index} {channel.Name} $end");
        writer.WriteLine("$upscope $end");
        writer.WriteLine("$enddefinitions $end");
        if (samples.Length == 0) return;

        writer.WriteLine("#0");
        var previous = samples[0];
        for (var channel = 0; channel < 16; channel++)
            writer.WriteLine($"{((previous & (1 << channel)) != 0 ? '1' : '0')} c{channel}");

        for (var i = 1; i < samples.Length; i++)
        {
            var current = samples[i];
            var changed = (ushort)(previous ^ current);
            if (changed == 0) continue;
            var timeNs = (ulong)Math.Round(i * 1_000_000_000d / window.SampleRateHz,
                MidpointRounding.AwayFromZero);
            writer.WriteLine($"#{timeNs.ToString(CultureInfo.InvariantCulture)}");
            for (var channel = 0; channel < 16; channel++)
            {
                var mask = (ushort)(1 << channel);
                if ((changed & mask) != 0)
                    writer.WriteLine($"{((current & mask) != 0 ? '1' : '0')} c{channel}");
            }
            previous = current;
        }
    }
}
