using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using LogicScope.Core.Models;

namespace LogicScope.Hardware.Protocol;

public abstract record DeviceWirePacket;
public sealed record DataWirePacket(DataPacket Packet) : DeviceWirePacket;
public sealed record StatusWirePacket(DeviceStatus Status) : DeviceWirePacket;

/// <summary>Byte-exact codec for LogicScope protocol v1.</summary>
public static class PacketCodec
{
    public const byte DataMarker = 0xAA;
    public const byte StatusMarker = 0x55;
    public const int CapsLength = 16;
    public const int DataHeaderLength = 8;
    public const int MaxSamplesPerPacket = 2048;

    public static byte[] EncodeStart(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var bytes = new byte[9];
        bytes[0] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1, 4), settings.SampleRateHz);
        bytes[5] = (byte)settings.Mode;
        bytes[6] = settings.TriggerChannel;
        bytes[7] = (byte)settings.Edge;
        bytes[8] = settings.PreTriggerPercent;
        return bytes;
    }

    public static byte[] EncodeStop() => [0x00];
    public static byte[] EncodeGetCaps() => [0x02];
    public static byte[] EncodeSelfTest() => [0x03];

    public static DeviceCapabilities DecodeCaps(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != CapsLength)
            throw new InvalidDataException($"GET_CAPS reply must be exactly {CapsLength} bytes.");
        return new DeviceCapabilities(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[0..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]),
            bytes[12], bytes[13], bytes[14], bytes[15]);
    }

    public static byte[] EncodeDataPacket(byte sequence, CaptureMode mode, byte flags,
        ReadOnlySpan<ushort> samples)
    {
        if (samples.Length > MaxSamplesPerPacket)
            throw new ArgumentOutOfRangeException(nameof(samples));
        var packet = new byte[DataHeaderLength + samples.Length * sizeof(ushort)];
        packet[0] = DataMarker;
        packet[1] = sequence;
        packet[2] = (byte)mode;
        packet[3] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)samples.Length);
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(DataHeaderLength + i * 2, 2), samples[i]);
        return packet;
    }

    public static DataPacket DecodeDataPacket(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < DataHeaderLength || bytes[0] != DataMarker)
            throw new InvalidDataException("Invalid data packet header.");
        if (bytes[2] > (byte)CaptureMode.Burst)
            throw new InvalidDataException("Unknown capture mode.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if (count > MaxSamplesPerPacket)
            throw new InvalidDataException("Data packet sample count exceeds protocol limit.");
        var expectedLength = checked(DataHeaderLength + (int)count * sizeof(ushort));
        if (bytes.Length != expectedLength)
            throw new InvalidDataException("Data packet length does not match its sample count.");
        var samples = new ushort[(int)count];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(DataHeaderLength + i * 2, 2));
        return new DataPacket(bytes[1], (CaptureMode)bytes[2], bytes[3], samples);
    }

    public static async IAsyncEnumerable<DeviceWirePacket> ReadPacketsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new BufferedByteReader(stream);
        while (true)
        {
            var marker = await reader.ReadByteOrEofAsync(cancellationToken).ConfigureAwait(false);
            if (marker < 0) yield break;
            if (marker == StatusMarker)
            {
                var tail = new byte[2];
                await reader.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
                yield return new StatusWirePacket(new DeviceStatus(tail[0], tail[1]));
                continue;
            }
            if (marker != DataMarker) continue;

            var header = new byte[DataHeaderLength];
            header[0] = DataMarker;
            await reader.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (header[2] > (byte)CaptureMode.Burst || count > MaxSamplesPerPacket)
            {
                // A bad candidate must not consume the following marker: return
                // the header tail to the byte scanner and advance one byte.
                reader.PushBack(header.AsSpan(1));
                continue;
            }
            var payload = new byte[checked((int)count * sizeof(ushort))];
            await reader.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            var frame = new byte[DataHeaderLength + payload.Length];
            header.CopyTo(frame, 0);
            payload.CopyTo(frame, DataHeaderLength);
            yield return new DataWirePacket(DecodeDataPacket(frame));
        }
    }

    private sealed class BufferedByteReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _offset;
        private int _count;

        public async ValueTask<int> ReadByteOrEofAsync(CancellationToken cancellationToken)
        {
            if (_offset == _count)
            {
                _count = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0) return -1;
            }
            return _buffer[_offset++];
        }

        public void PushBack(ReadOnlySpan<byte> bytes)
        {
            var remaining = _count - _offset;
            if (bytes.Length + remaining > _buffer.Length)
                throw new InvalidOperationException("Packet parser look-ahead buffer overflow.");
            if (remaining != 0)
                Buffer.BlockCopy(_buffer, _offset, _buffer, bytes.Length, remaining);
            bytes.CopyTo(_buffer);
            _offset = 0;
            _count = bytes.Length + remaining;
        }

        public async ValueTask ReadExactlyAsync(Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            var written = 0;
            while (written < destination.Length)
            {
                if (_offset < _count)
                {
                    var take = Math.Min(_count - _offset, destination.Length - written);
                    _buffer.AsMemory(_offset, take).CopyTo(destination[written..]);
                    _offset += take;
                    written += take;
                    continue;
                }
                _count = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0) throw new EndOfStreamException("USB serial stream ended mid-packet.");
            }
        }
    }
}
