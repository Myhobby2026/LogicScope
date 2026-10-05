using System.Buffers.Binary;
using System.Globalization;
using LogicScope.Core.Models;

namespace LogicScope.Export;

/// <summary>
/// Writes interleaved 16-bit little-endian samples for Saleae Logic 2's raw
/// binary import workflow. This is not an undocumented .sal/.logicdata archive.
/// </summary>
public static class SaleaeRawBinaryExport
{
    public static async Task WriteAsync(string path, SampleWindow window,
        CancellationToken cancellationToken = default)
    {
        if (window.SampleRateHz == 0) throw new ArgumentOutOfRangeException(nameof(window));
        var samples = window.Samples.ToArray();
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write,
                         FileShare.None, 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var bytes = new byte[64 * 1024];
            var offset = 0;
            while (offset < samples.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(bytes.Length / 2, samples.Length - offset);
                for (var i = 0; i < count; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[offset + i]);
                await file.WriteAsync(bytes.AsMemory(0, count * 2), cancellationToken).ConfigureAwait(false);
                offset += count;
            }
        }

        // A plain-text companion records the dialog settings needed to import
        // the raw binary reproducibly; Logic 2 does not consume this sidecar.
        var sidecar = path + ".import.txt";
        var settings = $"Format: unsigned 16-bit little-endian, one word per sample\n" +
                       $"Sample rate: {window.SampleRateHz.ToString(CultureInfo.InvariantCulture)} Hz\n" +
                       "Channels: 16 (D0 = bit 0, D15 = bit 15)\n" +
                       $"Samples: {samples.Length.ToString(CultureInfo.InvariantCulture)}\n";
        await File.WriteAllTextAsync(sidecar, settings, new System.Text.UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }
}
