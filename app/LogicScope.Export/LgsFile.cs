using System.Buffers.Binary;
using System.Text;
using LogicScope.Core.Models;
using ZstdSharp;

namespace LogicScope.Export;

public sealed record LgsCapture(uint SampleRateHz, ushort ChannelCount, ushort[] Samples);

/// <summary>
/// LogicScope native file v1: 40-byte little-endian header followed by a Zstandard
/// frame containing interleaved packed u16 samples. CRC-32 protects the logical
/// sample bytes against storage corruption.
/// </summary>
public static class LgsFile
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LGSCOPE1");
    private const ushort Version = 1;
    private const ushort HeaderSize = 40;
    private const ushort SampleFormatU16Le = 1;

    public static async Task SaveAsync(string path, SampleWindow window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.SampleRateHz == 0) throw new ArgumentOutOfRangeException(nameof(window));
        var samples = window.Samples.ToArray();
        var raw = new byte[checked(samples.Length * sizeof(ushort))];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * 2, 2), samples[i]);

        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(raw).ToArray();
        var header = BuildHeader(window.SampleRateHz, (ulong)window.Samples.Length,
            (uint)raw.Length, (uint)compressed.Length, Crc32.Compute(raw));

        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await file.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await file.WriteAsync(compressed, cancellationToken).ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<LgsCapture> LoadAsync(string path,
        CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[HeaderSize];
        await ReadExactlyAsync(file, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("Not a LogicScope .lgs file.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2)) != Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10, 2)) != HeaderSize)
            throw new InvalidDataException("Unsupported LogicScope file version/header size.");

        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16, 2));
        var sampleFormat = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2));
        var samples = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(20, 8));
        var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28, 4));
        var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32, 4));
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36, 4));
        if (sampleRate == 0 || channels != 16 || sampleFormat != SampleFormatU16Le ||
            samples > int.MaxValue || rawLength != samples * sizeof(ushort) ||
            compressedLength > file.Length - HeaderSize)
            throw new InvalidDataException("Invalid LogicScope file dimensions.");

        if (file.Length != HeaderSize + (long)compressedLength)
            throw new InvalidDataException("Trailing bytes or truncated compressed payload in .lgs file.");
        var compressed = new byte[checked((int)compressedLength)];
        await ReadExactlyAsync(file, compressed, cancellationToken).ConfigureAwait(false);
        using var decompressor = new Decompressor();
        var raw = decompressor.Unwrap(compressed).ToArray();
        if (raw.Length != rawLength || Crc32.Compute(raw) != expectedCrc)
            throw new InvalidDataException("LogicScope sample payload failed length/CRC validation.");
        var values = new ushort[(int)samples];
        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2, 2));
        return new LgsCapture(sampleRate, channels, values);
    }

    private static byte[] BuildHeader(uint rate, ulong samples, uint rawBytes,
        uint compressedBytes, uint crc)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10, 2), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), rate);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16, 2), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18, 2), SampleFormatU16Le);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(20, 8), samples);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), rawBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32, 4), compressedBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), crc);
        return header;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Unexpected end of .lgs file.");
            offset += count;
        }
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes) crc = Table[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
