using System.Globalization;
using LogicScope.Core.Models;

namespace LogicScope.Export;

public static class CsvExport
{
    public static async Task WriteSamplesAsync(string path, SampleWindow window,
        IReadOnlyList<ChannelDefinition>? channels = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        channels ??= ChannelDefinition.DefaultChannels;
        if (window.SampleRateHz == 0) throw new ArgumentOutOfRangeException(nameof(window));
        if (channels.Count != 16) throw new ArgumentException("Exactly 16 channel definitions are required.");
        var orderedChannels = channels.OrderBy(c => c.Index).ToArray();
        if (orderedChannels.Select(c => c.Index).Distinct().Count() != 16 ||
            orderedChannels.Any(c => (uint)c.Index >= 16))
            throw new ArgumentException("Channel indexes must contain each value from 0 through 15.");
        var samples = window.Samples.ToArray();
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        var header = new List<string> { "sample_index", "time_seconds" };
        header.AddRange(orderedChannels.Select(c => Escape(c.Name)));
        await writer.WriteLineAsync(string.Join(",", header)).ConfigureAwait(false);

        for (var i = 0; i < samples.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = samples[i];
            var fields = new string[18];
            fields[0] = (window.StartSample + i).ToString(CultureInfo.InvariantCulture);
            fields[1] = ((window.StartSample + i) / (double)window.SampleRateHz)
                .ToString("G17", CultureInfo.InvariantCulture);
            for (var channel = 0; channel < 16; channel++)
                fields[channel + 2] = (sample >> channel & 1).ToString(CultureInfo.InvariantCulture);
            await writer.WriteLineAsync(string.Join(",", fields)).ConfigureAwait(false);
        }
    }

    public static async Task WriteDecoderResultsAsync(string path,
        IEnumerable<DecoderResult> results, uint sampleRateHz,
        CancellationToken cancellationToken = default)
    {
        if (sampleRateHz == 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.None, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteLineAsync("start_sample,end_sample,start_seconds,end_seconds,category,error,label")
            .ConfigureAwait(false);
        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = string.Join(',',
                result.StartSample.ToString(CultureInfo.InvariantCulture),
                result.EndSample.ToString(CultureInfo.InvariantCulture),
                (result.StartSample / (double)sampleRateHz).ToString("G17", CultureInfo.InvariantCulture),
                (result.EndSample / (double)sampleRateHz).ToString("G17", CultureInfo.InvariantCulture),
                Escape(result.Category), result.IsError ? "true" : "false", Escape(result.Label));
            await writer.WriteLineAsync(row).ConfigureAwait(false);
        }
    }

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
