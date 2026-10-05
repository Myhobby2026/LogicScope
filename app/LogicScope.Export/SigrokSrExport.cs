using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Buffers.Binary;
using LogicScope.Core.Models;

namespace LogicScope.Export;

public static class SigrokSrExport
{
    public static async Task WriteAsync(string path, SampleWindow window,
        IReadOnlyList<ChannelDefinition>? channels = null,
        CancellationToken cancellationToken = default)
    {
        if (window.SampleRateHz == 0) throw new ArgumentOutOfRangeException(nameof(window));
        channels ??= ChannelDefinition.DefaultChannels;
        var ordered = channels.OrderBy(c => c.Index).ToArray();
        if (ordered.Length != 16 || ordered.Select(c => c.Index).Distinct().Count() != 16 ||
            ordered.Any(c => (uint)c.Index >= 16))
            throw new ArgumentException("Sigrok export needs 16 uniquely indexed channels.");

        var metadata = new StringBuilder()
            .AppendLine("[global]")
            .AppendLine("sigrok version=0.6.0")
            .AppendLine()
            .AppendLine("[device 1]")
            .AppendLine("capturefile=logic-1")
            .AppendLine("unitsize=2")
            .AppendLine("total probes=16")
            .AppendLine("total analog=0")
            .Append("samplerate=").AppendLine(FormatRate(window.SampleRateHz));
        foreach (var channel in ordered)
            metadata.Append("probe").Append(channel.Index + 1).Append('=').AppendLine(channel.Name);

        var samples = window.Samples.ToArray();
        await using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(archive, "version", Encoding.ASCII.GetBytes("2\n"), cancellationToken)
                .ConfigureAwait(false);
            await WriteEntryAsync(archive, "metadata", Encoding.UTF8.GetBytes(metadata.ToString()), cancellationToken)
                .ConfigureAwait(false);
            var logic = archive.CreateEntry("logic-1", CompressionLevel.Optimal);
            await using var output = logic.Open();
            var byteBuffer = new byte[64 * 1024];
            var sampleOffset = 0;
            while (sampleOffset < samples.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(byteBuffer.Length / sizeof(ushort), samples.Length - sampleOffset);
                for (var i = 0; i < count; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(byteBuffer.AsSpan(i * 2, 2),
                        samples[sampleOffset + i]);
                await output.WriteAsync(byteBuffer.AsMemory(0, count * 2), cancellationToken)
                    .ConfigureAwait(false);
                sampleOffset += count;
            }
        }
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name,
        byte[] data, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private static string FormatRate(uint rate)
    {
        if (rate % 1_000_000 == 0)
            return $"{(rate / 1_000_000d).ToString(CultureInfo.InvariantCulture)} MHz";
        if (rate % 1_000 == 0)
            return $"{(rate / 1_000d).ToString(CultureInfo.InvariantCulture)} kHz";
        return $"{rate.ToString(CultureInfo.InvariantCulture)} Hz";
    }
}
