using LogicScope.Core.Models;
using LogicScope.Decode;
using Xunit;

namespace LogicScope.Tests;

public sealed class DecoderGoldenVectorTests
{
    [Fact]
    public void I2cDecodesAddressDataAndAck()
    {
        var samples = BuildI2c(0xA0);
        var results = new I2cDecoder(0, 1).Decode(new SampleWindow(0, 1_000_000, samples));
        Assert.Contains(results, result => result.Label == "ADDR 0x50 W ACK");
        Assert.Contains(results, result => result.Label == "STOP");
    }

    [Fact]
    public void SpiDecodesMosiAndMisoWithModeZero()
    {
        var samples = BuildSpi(0xA5, 0x3C);
        var results = new SpiDecoder(0, 1, 2, 3, cpol: false, cpha: false).Decode(
            new SampleWindow(0, 1_000_000, samples));
        Assert.Contains(results, result => result.Label == "MOSI 0xA5 / MISO 0x3C");
    }

    [Fact]
    public void UartDecodesLsbFirstEightNOne()
    {
        var samples = BuildUart(0xA5, 10);
        var result = Assert.Single(new UartDecoder(0, 100_000, 8, UartParity.None, 1)
            .Decode(new SampleWindow(0, 1_000_000, samples)));
        Assert.Contains("0xA5", result.Label);
        Assert.False(result.IsError);
    }

    [Fact]
    public void CanDecoderParsesStandardZeroLengthFrame()
    {
        var samples = BuildCanStandard(0x123);
        var results = new CanDecoder(0, 1_000_000).Decode(new SampleWindow(0, 10_000_000, samples));
        Assert.Contains(results, result => result.Label.Contains("ID=0x123", StringComparison.Ordinal));
    }

    [Fact]
    public void OneWireFindsResetPresenceAndByte()
    {
        var samples = BuildOneWire(0xA5);
        var results = new OneWireDecoder(0).Decode(new SampleWindow(0, 1_000_000, samples));
        Assert.Contains(results, result => result.Label.StartsWith("RESET", StringComparison.Ordinal));
        Assert.Contains(results, result => result.Label.StartsWith("PRESENCE", StringComparison.Ordinal));
        Assert.Contains(results, result => result.Label == "0xA5");
    }

    [Fact]
    public void Ps2DecodesOddParityFrame()
    {
        var samples = BuildPs2(0xA5);
        var results = new Ps2Decoder(0, 1).Decode(new SampleWindow(0, 1_000_000, samples));
        var result = Assert.Single(results.Where(item => item.Label.StartsWith("0xA5", StringComparison.Ordinal)));
        Assert.False(result.IsError);
    }

    [Fact]
    public void ManchesterDecodesByteWithMidBitTransitions()
    {
        var samples = BuildManchester(0xA5);
        var results = new ManchesterDecoder(0, 250_000, risingTransitionIsOne: true)
            .Decode(new SampleWindow(0, 1_000_000, samples));
        Assert.Contains(results, result => result.Label == "0xA5");
    }

    private static ushort[] BuildI2c(byte addressByte)
    {
        var samples = new List<ushort> { 0b11 };
        Add(0, 1); // START: SDA falls while SCL remains high.
        for (var bit = 7; bit >= 0; bit--) ClockBit(((addressByte >> bit) & 1) != 0);
        ClockBit(false); // ACK
        Add(0, 0);
        Add(0, 1);
        Add(1, 1); // STOP
        return samples.ToArray();

        void ClockBit(bool sda)
        {
            Add(sda ? 1 : 0, 0);
            Add(sda ? 1 : 0, 1);
        }
        void Add(int sda, int scl) => samples.Add((ushort)(sda | (scl << 1)));
    }

    private static ushort[] BuildSpi(byte mosi, byte miso)
    {
        var samples = new List<ushort> { 1 << 3 };
        Add(false, false, false, false); // assert active-low CS
        for (var bit = 7; bit >= 0; bit--)
        {
            var outBit = ((mosi >> bit) & 1) != 0;
            var inBit = ((miso >> bit) & 1) != 0;
            Add(false, outBit, inBit, false);
            Add(true, outBit, inBit, false); // CPHA 0 samples leading rising edge
            Add(false, outBit, inBit, false);
        }
        Add(false, false, false, true);
        return samples.ToArray();

        void Add(bool clock, bool dataOut, bool dataIn, bool cs)
        {
            ushort value = 0;
            if (clock) value |= 1 << 0;
            if (dataOut) value |= 1 << 1;
            if (dataIn) value |= 1 << 2;
            if (cs) value |= 1 << 3;
            samples.Add(value);
        }
    }

    private static ushort[] BuildUart(byte value, int samplesPerBit)
    {
        var samples = new List<ushort>();
        Add(true, samplesPerBit);
        Add(false, samplesPerBit); // start
        for (var bit = 0; bit < 8; bit++) Add(((value >> bit) & 1) != 0, samplesPerBit);
        Add(true, samplesPerBit); // stop
        Add(true, samplesPerBit);
        return samples.ToArray();

        void Add(bool high, int count)
        {
            for (var i = 0; i < count; i++) samples.Add(high ? (ushort)1 : (ushort)0);
        }
    }

    private static ushort[] BuildPs2(byte value)
    {
        var bits = new List<bool> { false };
        for (var bit = 0; bit < 8; bit++) bits.Add(((value >> bit) & 1) != 0);
        var oddParity = (System.Numerics.BitOperations.PopCount((uint)value) & 1) == 0;
        bits.Add(oddParity);
        bits.Add(true);
        var samples = new List<ushort> { 1 };
        foreach (var bit in bits)
        {
            samples.Add((ushort)(1 | (bit ? 2 : 0))); // clock high
            samples.Add((ushort)(bit ? 2 : 0));       // falling edge samples data
        }
        return samples.ToArray();
    }

    private static ushort[] BuildManchester(byte value)
    {
        var samples = new List<ushort>();
        for (var bit = 7; bit >= 0; bit--)
        {
            var one = ((value >> bit) & 1) != 0;
            var firstHalf = !one;
            var secondHalf = one;
            samples.AddRange(Enumerable.Repeat(firstHalf ? (ushort)1 : (ushort)0, 2));
            samples.AddRange(Enumerable.Repeat(secondHalf ? (ushort)1 : (ushort)0, 2));
        }
        return samples.ToArray();
    }

    private static ushort[] BuildOneWire(byte value)
    {
        var samples = new List<ushort>();
        AddHigh(20);
        AddLow(500); AddHigh(100); // reset
        AddLow(100); AddHigh(100); // presence
        for (var bit = 0; bit < 8; bit++)
        {
            var one = ((value >> bit) & 1) != 0;
            AddLow(one ? 6 : 60);
            AddHigh(20);
        }
        return samples.ToArray();

        void AddLow(int count) { for (var i = 0; i < count; i++) samples.Add(0); }
        void AddHigh(int count) { for (var i = 0; i < count; i++) samples.Add(1); }
    }

    private static ushort[] BuildCanStandard(int identifier)
    {
        var logical = new List<bool> { false };
        for (var bit = 10; bit >= 0; bit--) logical.Add(((identifier >> bit) & 1) != 0);
        logical.Add(false); // RTR
        logical.Add(false); // IDE (standard)
        logical.Add(false); // r0
        logical.AddRange([false, false, false, false]); // DLC = 0
        var crc = CanCrc(logical);
        for (var bit = 14; bit >= 0; bit--) logical.Add(((crc >> bit) & 1) != 0);

        var stuffed = new List<bool>();
        var prior = false;
        var run = 0;
        foreach (var bit in logical)
        {
            stuffed.Add(bit);
            if (run == 0 || bit != prior) run = 1;
            else run++;
            prior = bit;
            if (run == 5)
            {
                stuffed.Add(!bit);
                run = 0;
            }
        }
        stuffed.Add(true);  // CRC delimiter
        stuffed.Add(false); // ACK slot
        stuffed.Add(true);  // ACK delimiter
        for (var i = 0; i < 10; i++) stuffed.Add(true); // EOF + intermission

        const int samplesPerBit = 10;
        var samples = Enumerable.Repeat((ushort)1, 20).ToList(); // recessive bus before SOF
        foreach (var bit in stuffed)
            samples.AddRange(Enumerable.Repeat(bit ? (ushort)1 : (ushort)0, samplesPerBit));
        return samples.ToArray();
    }

    private static ushort CanCrc(IReadOnlyList<bool> bits)
    {
        ushort crc = 0;
        foreach (var bit in bits)
        {
            var feedback = (((crc >> 14) & 1) != 0) ^ bit;
            crc = (ushort)((crc << 1) & 0x7FFF);
            if (feedback) crc ^= 0x4599;
        }
        return crc;
    }
}
