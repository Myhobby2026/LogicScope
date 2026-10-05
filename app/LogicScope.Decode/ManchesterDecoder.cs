using LogicScope.Core.Models;

namespace LogicScope.Decode;

public sealed class ManchesterDecoder(
    int channel,
    uint bitRate,
    bool risingTransitionIsOne = true,
    int startOffsetSamples = 0,
    bool msbFirst = true) : ProtocolDecoder
{
    public override string Name => "Manchester";

    protected override IEnumerable<DecoderResult> DecodeCore(SampleWindow window)
    {
        if ((uint)channel >= 16) throw new ArgumentOutOfRangeException(nameof(channel));
        if (bitRate == 0) throw new ArgumentOutOfRangeException(nameof(bitRate));
        var samplesPerBit = window.SampleRateHz / (double)bitRate;
        if (samplesPerBit < 2) yield break;
        if (startOffsetSamples < 0) throw new ArgumentOutOfRangeException(nameof(startOffsetSamples));

        var bitValues = new List<bool>(8);
        var byteStart = startOffsetSamples;
        var endOfLastBit = 0;
        var byteValue = 0;
        var bitIndex = 0;
        for (var cell = startOffsetSamples; cell + samplesPerBit < window.Samples.Length;
             cell = (int)Math.Floor(startOffsetSamples + (++bitIndex) * samplesPerBit))
        {
            var first = Level(window, channel,
                (int)Math.Round(cell + samplesPerBit * 0.25d, MidpointRounding.AwayFromZero));
            var second = Level(window, channel,
                (int)Math.Round(cell + samplesPerBit * 0.75d, MidpointRounding.AwayFromZero));
            var cellEnd = (int)Math.Ceiling(cell + samplesPerBit);
            if (first == second)
            {
                yield return new DecoderResult(AbsoluteSample(window, cell),
                    AbsoluteSample(window, Math.Min(cellEnd, window.Samples.Length - 1)),
                    "missing mid-bit transition", "error", true);
                bitValues.Clear();
                byteValue = 0;
                byteStart = cellEnd;
                endOfLastBit = cellEnd;
                continue;
            }

            var bit = risingTransitionIsOne ? (!first && second) : (first && !second);
            var position = bitValues.Count;
            if (msbFirst) byteValue = (byteValue << 1) | (bit ? 1 : 0);
            else if (bit) byteValue |= 1 << position;
            bitValues.Add(bit);
            endOfLastBit = cellEnd;
            if (bitValues.Count == 8)
            {
                yield return new DecoderResult(AbsoluteSample(window, byteStart),
                    AbsoluteSample(window, Math.Min(endOfLastBit, window.Samples.Length - 1)),
                    $"0x{byteValue:X2}", "data");
                bitValues.Clear();
                byteValue = 0;
                byteStart = cellEnd;
            }
        }

        if (bitValues.Count > 0)
            yield return new DecoderResult(AbsoluteSample(window, byteStart),
                AbsoluteSample(window, Math.Min(endOfLastBit, window.Samples.Length - 1)),
                $"{string.Concat(bitValues.Select(x => x ? '1' : '0'))} (partial)", "data");
    }
}
