using LogicScope.Core.Models;

namespace LogicScope.Decode;

public sealed class I2cDecoder(int sdaChannel, int sclChannel) : ProtocolDecoder
{
    public override string Name => "I²C";

    protected override IEnumerable<DecoderResult> DecodeCore(SampleWindow window)
    {
        CheckChannel(sdaChannel);
        CheckChannel(sclChannel);
        var active = false;
        var firstByte = true;
        var bitCount = 0;
        var value = 0;
        var byteStart = 0;
        var previousSda = Level(window, sdaChannel, 0);
        var previousScl = Level(window, sclChannel, 0);

        for (var i = 1; i < window.Samples.Length; i++)
        {
            var sda = Level(window, sdaChannel, i);
            var scl = Level(window, sclChannel, i);
            var start = previousSda && !sda && previousScl && scl;
            var stop = !previousSda && sda && previousScl && scl;

            if (start)
            {
                active = true;
                firstByte = true;
                bitCount = 0;
                value = 0;
                byteStart = i;
                yield return new DecoderResult(AbsoluteSample(window, i),
                    AbsoluteSample(window, i), "START", "start");
            }
            else if (stop && active)
            {
                if (bitCount != 0)
                    yield return new DecoderResult(AbsoluteSample(window, byteStart),
                        AbsoluteSample(window, i), "truncated byte", "error", true);
                yield return new DecoderResult(AbsoluteSample(window, i),
                    AbsoluteSample(window, i), "STOP", "stop");
                active = false;
                bitCount = 0;
            }

            if (active && !previousScl && scl)
            {
                if (bitCount < 8)
                {
                    value = ((value << 1) | (sda ? 1 : 0)) & 0xFF;
                    bitCount++;
                }
                else
                {
                    var ack = !sda;
                    var label = firstByte
                        ? $"ADDR 0x{value >> 1:X2} {(value & 1) == 0 ? 'W' : 'R'} {(ack ? "ACK" : "NACK")}"
                        : $"0x{value:X2} {(ack ? "ACK" : "NACK")}";
                    yield return new DecoderResult(AbsoluteSample(window, byteStart),
                        AbsoluteSample(window, i), label,
                        firstByte ? "address" : "data", !ack);
                    firstByte = false;
                    bitCount = 0;
                    value = 0;
                    byteStart = i + 1;
                }
            }

            previousSda = sda;
            previousScl = scl;
        }
    }

    private static void CheckChannel(int channel)
    {
        if ((uint)channel >= 16) throw new ArgumentOutOfRangeException(nameof(channel));
    }
}
