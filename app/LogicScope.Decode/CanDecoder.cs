using System.Text;
using LogicScope.Core.Models;

namespace LogicScope.Decode;

/// <summary>
/// Sample-clock CAN 2.0 decoder. CAN-FD frames are deliberately surfaced as
/// de-stuffed raw bit strings rather than claiming FD CRC/timing validation.
/// </summary>
public sealed class CanDecoder(int rxChannel, uint nominalBitRate) : ProtocolDecoder
{
    private const ushort Crc15Polynomial = 0x4599;
    public override string Name => "CAN 2.0 / FD raw";

    protected override IEnumerable<DecoderResult> DecodeCore(SampleWindow window)
    {
        if ((uint)rxChannel >= 16) throw new ArgumentOutOfRangeException(nameof(rxChannel));
        if (nominalBitRate == 0) throw new ArgumentOutOfRangeException(nameof(nominalBitRate));
        var samplesPerBit = window.SampleRateHz / (double)nominalBitRate;
        if (samplesPerBit < 2) yield break;

        var previous = Level(window, rxChannel, 0);
        for (var i = 1; i < window.Samples.Length; i++)
        {
            var level = Level(window, rxChannel, i);
            if (previous && !level)
            {
                var sof = i - 0.5d;
                if (LevelAt(sof + samplesPerBit * 0.5d))
                {
                    previous = level;
                    continue;
                }

                var bits = ReadDestuffedFrame(window, sof, samplesPerBit, out var frameEnd);
                if (bits.Count >= 20)
                {
                    var decoded = DecodeFrame(bits, window, sof, frameEnd);
                    yield return decoded;
                    i = Math.Max(i, (int)Math.Ceiling(frameEnd));
                    previous = Level(window, rxChannel, Math.Min(i, window.Samples.Length - 1));
                    continue;
                }
            }
            previous = level;
        }

        bool LevelAt(double local) => Level(window, rxChannel,
            (int)Math.Round(local, MidpointRounding.AwayFromZero));
    }

    private List<bool> ReadDestuffedFrame(SampleWindow window, double sof,
        double samplesPerBit, out double frameEnd)
    {
        var bits = new List<bool>(128);
        var previousDataBit = false;
        var sameRun = 0;
        var sampleIndex = sof;
        frameEnd = sof;

        for (var bitIndex = 0; bitIndex < 1600; bitIndex++)
        {
            var center = sof + (bitIndex + 0.5d) * samplesPerBit;
            if (center >= window.Samples.Length) break;
            var bit = Level(window, rxChannel,
                (int)Math.Round(center, MidpointRounding.AwayFromZero));
            sampleIndex = center;

            if (sameRun == 5)
            {
                // CAN bit stuffing inserts the complement after five identical
                // transmitted bits. A violation terminates this candidate frame.
                if (bit == previousDataBit) break;
                sameRun = 0;
                continue;
            }

            bits.Add(bit);
            if (bits.Count == 1 || bit != previousDataBit)
                sameRun = 1;
            else
                sameRun++;
            previousDataBit = bit;

            // Seven recessive EOF bits cannot occur in a stuffed header/data/CRC
            // sequence. The three recessive intermission bits may also be read.
            if (bits.Count >= 20 && bit && sameRun >= 7)
            {
                frameEnd = center;
                break;
            }
        }
        if (frameEnd == sof) frameEnd = sampleIndex;
        return bits;
    }

    private static DecoderResult DecodeFrame(IReadOnlyList<bool> bits,
        SampleWindow window, double sof, double frameEnd)
    {
        if (bits.Count < 15)
            return Error("short CAN frame", window, sof, frameEnd);

        var extended = bits[13];
        var rtrIndex = extended ? 32 : 12;
        var fdMarkerIndex = extended ? 33 : 14;
        var fd = bits.Count > fdMarkerIndex && bits[fdMarkerIndex];
        if (fd)
        {
            var raw = new StringBuilder(Math.Min(bits.Count, 128));
            foreach (var bit in bits.Take(128)) raw.Append(bit ? '1' : '0');
            return new DecoderResult(AbsoluteSample(window, sof),
                AbsoluteSample(window, frameEnd), $"CAN-FD raw {raw}", "raw");
        }

        var dlcIndex = extended ? 35 : 15;
        var dataStart = dlcIndex + 4;
        if (bits.Count < dataStart)
            return Error("truncated CAN control field", window, sof, frameEnd);
        var id = extended
            ? (ReadBits(bits, 1, 11) << 18) | ReadBits(bits, 14, 18)
            : ReadBits(bits, 1, 11);
        var dlc = ReadBits(bits, dlcIndex, 4);
        var remote = bits.Count > rtrIndex && bits[rtrIndex];
        if (dlc > 8)
            return new DecoderResult(AbsoluteSample(window, sof), AbsoluteSample(window, frameEnd),
                $"CAN {(extended ? "EXT" : "STD")} ID=0x{id:X} DLC={dlc} (reserved)", "error", true);

        var dataLength = remote ? 0 : dlc;
        var crcStart = dataStart + dataLength * 8;
        var crcPresent = bits.Count >= crcStart + 15;
        var crcError = crcPresent && ComputeCrc15(bits, crcStart) != ReadBits(bits, crcStart, 15);
        var payload = new StringBuilder();
        for (var b = 0; b < dataLength && dataStart + b * 8 + 7 < bits.Count; b++)
            payload.Append(ReadBits(bits, dataStart + b * 8, 8).ToString("X2"));
        var label = $"CAN {(extended ? "EXT" : "STD")} ID=0x{id:X}{(remote ? " RTR" : string.Empty)} DLC={dlc}";
        if (payload.Length > 0) label += $" [{payload}]";
        if (crcError) label += " CRC!";
        if (!crcPresent) label += " CRC truncated";
        return new DecoderResult(AbsoluteSample(window, sof), AbsoluteSample(window, frameEnd),
            label, crcError ? "error" : "data", crcError);
    }

    private static ushort ComputeCrc15(IReadOnlyList<bool> bits, int count)
    {
        ushort crc = 0;
        for (var i = 0; i < count; i++)
        {
            var feedback = (((crc >> 14) & 1) != 0) ^ bits[i];
            crc = (ushort)((crc << 1) & 0x7FFF);
            if (feedback) crc ^= Crc15Polynomial;
        }
        return crc;
    }

    private static int ReadBits(IReadOnlyList<bool> bits, int start, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
            value = (value << 1) | (start + i < bits.Count && bits[start + i] ? 1 : 0);
        return value;
    }

    private static DecoderResult Error(string text, SampleWindow window, double start, double end) =>
        new(AbsoluteSample(window, start), AbsoluteSample(window, end), text, "error", true);
}
