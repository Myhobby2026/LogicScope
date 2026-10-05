using System.Numerics;
using LogicScope.Core.Models;

namespace LogicScope.Decode;

public enum UartParity { None, Even, Odd, Mark, Space }

public sealed class UartDecoder(
    int channel,
    uint baudRate,
    int dataBits = 8,
    UartParity parity = UartParity.None,
    double stopBits = 1,
    bool inverted = false) : ProtocolDecoder
{
    public override string Name => "UART";

    protected override IEnumerable<DecoderResult> DecodeCore(SampleWindow window)
    {
        CheckChannel(channel);
        if (baudRate == 0) throw new ArgumentOutOfRangeException(nameof(baudRate));
        if (dataBits is < 5 or > 9) throw new ArgumentOutOfRangeException(nameof(dataBits));
        if (stopBits is not (1 or 1.5 or 2)) throw new ArgumentOutOfRangeException(nameof(stopBits));

        var samplesPerBit = window.SampleRateHz / (double)baudRate;
        if (samplesPerBit < 2) yield break;
        var idleHigh = true;
        var priorLogical = LogicalLevel(0);
        var parityBits = parity == UartParity.None ? 0 : 1;

        for (var i = 1; i < window.Samples.Length; i++)
        {
            var current = LogicalLevel(i);
            if (priorLogical == idleHigh && current != idleHigh)
            {
                var start = i - 0.5d;
                // Reject a short low glitch by checking the center of the start bit.
                if (LogicalAt(start + samplesPerBit / 2d) == idleHigh)
                {
                    priorLogical = current;
                    continue;
                }

                var value = 0;
                for (var bit = 0; bit < dataBits; bit++)
                {
                    var local = start + (1.5d + bit) * samplesPerBit;
                    if (LogicalAt(local)) value |= 1 << bit;
                }

                var parityError = false;
                if (parityBits != 0)
                {
                    var paritySample = LogicalAt(start + (1.5d + dataBits) * samplesPerBit);
                    var onesOdd = (BitOperations.PopCount((uint)value) & 1) != 0;
                    var expected = parity switch
                    {
                        UartParity.Even => onesOdd,
                        UartParity.Odd => !onesOdd,
                        UartParity.Mark => true,
                        UartParity.Space => false,
                        _ => paritySample
                    };
                    parityError = paritySample != expected;
                }

                var stopStart = 1d + dataBits + parityBits;
                var stopError = false;
                for (var stop = 0; stop < stopBits; stop++)
                {
                    var local = start + (stopStart + stop + 0.5d) * samplesPerBit;
                    if (!LogicalAt(local)) stopError = true;
                }

                var end = Math.Min(window.Samples.Length - 1d,
                    start + (1d + dataBits + parityBits + stopBits) * samplesPerBit);
                var printable = value is >= 32 and <= 126 ? $" '{(char)value}'" : string.Empty;
                var error = parityError || stopError;
                var suffix = parityError ? " parity!" : string.Empty;
                if (stopError) suffix += " framing!";
                yield return new DecoderResult(AbsoluteSample(window, start),
                    AbsoluteSample(window, end), $"0x{value:X2}{printable}{suffix}",
                    error ? "error" : "data", error);

                // Do not mistake data bits for later start edges in this frame.
                i = Math.Max(i, (int)Math.Floor(start +
                    (1d + dataBits + parityBits + stopBits) * samplesPerBit) - 1);
                current = LogicalAt(i);
            }
            priorLogical = current;
        }

        bool LogicalLevel(int index) => Level(window, channel, index) ^ inverted;
        bool LogicalAt(double localIndex)
        {
            var index = (int)Math.Round(localIndex, MidpointRounding.AwayFromZero);
            return LogicalLevel(index);
        }
    }

    private static void CheckChannel(int value)
    {
        if ((uint)value >= 16) throw new ArgumentOutOfRangeException(nameof(value));
    }
}
