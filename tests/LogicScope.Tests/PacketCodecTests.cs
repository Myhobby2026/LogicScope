using LogicScope.Core.Models;
using LogicScope.Hardware.Protocol;
using Xunit;

namespace LogicScope.Tests;

public sealed class PacketCodecTests
{
    [Fact]
    public void StartCommandMatchesLittleEndianGoldenVector()
    {
        var bytes = PacketCodec.EncodeStart(new CaptureSettings(2_000_000,
            CaptureMode.Burst, 3, TriggerEdge.Rising, 25));
        Assert.Equal(new byte[] { 0x01, 0x80, 0x84, 0x1E, 0x00, 0x01, 0x03, 0x01, 0x19 }, bytes);
    }

    [Fact]
    public void StopCapsAndSelfTestOpcodesAreByteExact()
    {
        Assert.Equal(new byte[] { 0x00 }, PacketCodec.EncodeStop());
        Assert.Equal(new byte[] { 0x02 }, PacketCodec.EncodeGetCaps());
        Assert.Equal(new byte[] { 0x03 }, PacketCodec.EncodeSelfTest());
    }

    [Fact]
    public void DataPacketMatchesGoldenVectorAndRoundTrips()
    {
        var encoded = PacketCodec.EncodeDataPacket(7, CaptureMode.Burst, 0x02,
            new ushort[] { 0x1234, 0xABCD });
        Assert.Equal(new byte[]
        {
            0xAA, 0x07, 0x01, 0x02, 0x02, 0x00, 0x00, 0x00,
            0x34, 0x12, 0xCD, 0xAB
        }, encoded);
        var decoded = PacketCodec.DecodeDataPacket(encoded);
        Assert.Equal((byte)7, decoded.Sequence);
        Assert.Equal(CaptureMode.Burst, decoded.Mode);
        Assert.True(decoded.Triggered);
        Assert.Equal(new ushort[] { 0x1234, 0xABCD }, decoded.Samples);
    }

    [Fact]
    public void CapsBlockDecodesFixedFields()
    {
        byte[] caps =
        [
            0x00, 0x2D, 0x31, 0x01, // 20,000,000
            0x80, 0x96, 0x98, 0x00, // 10,000,000
            0x00, 0x00, 0x02, 0x00, // 131,072
            0x01, 0x02, 0x03, 0x10
        ];
        var parsed = PacketCodec.DecodeCaps(caps);
        Assert.Equal(20_000_000u, parsed.MaxBurstRateHz);
        Assert.Equal(10_000_000u, parsed.MaxStreamRateHz);
        Assert.Equal(131_072u, parsed.RamSamples);
        Assert.Equal("1.2.3", parsed.FirmwareVersion);
        Assert.Equal(16, parsed.HardwareChannels);
    }

    [Fact]
    public async Task StreamParserSkipsNoiseAndReadsStatusThenData()
    {
        var frame = PacketCodec.EncodeDataPacket(4, CaptureMode.Streaming, 0,
            new ushort[] { 0x0001, 0x8000 });
        var bytes = new byte[] { 0x99, 0x00, 0x55, 1, 0 };
        bytes = bytes.Concat(frame).ToArray();
        var packets = new List<DeviceWirePacket>();
        await foreach (var packet in PacketCodec.ReadPacketsAsync(new MemoryStream(bytes)))
            packets.Add(packet);
        Assert.Equal(2, packets.Count);
        Assert.IsType<StatusWirePacket>(packets[0]);
        var data = Assert.IsType<DataWirePacket>(packets[1]).Packet;
        Assert.Equal(new ushort[] { 1, 0x8000 }, data.Samples);
    }

    [Fact]
    public async Task StreamParserResynchronizesAfterMalformedHeader()
    {
        var valid = PacketCodec.EncodeDataPacket(2, CaptureMode.Streaming, 0,
            new ushort[] { 0xCAFE });
        var bytes = new byte[]
        {
            0xAA, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F, // invalid candidate
            0x55, 1, 0 // valid status immediately after the bad header
        }.Concat(valid).ToArray();
        var packets = new List<DeviceWirePacket>();
        await foreach (var packet in PacketCodec.ReadPacketsAsync(new MemoryStream(bytes)))
            packets.Add(packet);
        Assert.Equal(2, packets.Count);
        Assert.IsType<StatusWirePacket>(packets[0]);
        Assert.Equal(new ushort[] { 0xCAFE }, Assert.IsType<DataWirePacket>(packets[1]).Packet.Samples);
    }

    [Fact]
    public void DecoderRejectsUnboundedSampleCount()
    {
        var malformed = new byte[] { 0xAA, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F };
        Assert.Throws<InvalidDataException>(() => PacketCodec.DecodeDataPacket(malformed));
    }
}
