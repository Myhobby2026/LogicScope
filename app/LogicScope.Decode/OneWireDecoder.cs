using LogicScope.Core.Models;

namespace LogicScope.Decode;

public sealed class OneWireDecoder(int dataChannel) : ProtocolDecoder
{
    public override string Name => "1-Wire";

    protected override IEnumerable<DecoderResult> DecodeCore(SampleWindow window)
    {
        if ((uint)dataChannel >= 16) throw new ArgumentOutOfRangeException(nameof(dataChannel));
        var edges = Transitions(window, dataChannel).ToArray();
        var bits = new List<int>(8);
        var byteStart = 0;
        var afterReset = false;
        var samplesPerMicrosecond = window.SampleRateHz / 1_000_000d;

        for (var i = 0; i + 1 < edges.Length; i++)
        {
            if (edges[i].Rising) continue;
            var lowStart = edges[i].Index;
            var lowEnd = edges[i + 1].Index;
            var lowUs = (lowEnd - lowStart) / samplesPerMicrosecond;

            if (lowUs >= 400)
            {
                if (bits.Count > 0)
                {
                    yield return new DecoderResult(AbsoluteSample(window, byteStart),
                        AbsoluteSample(window, lowStart), "partial byte", "error", true);
                    bits.Clear();
                }
                afterReset = true;
                yield return new DecoderResult(AbsoluteSample(window, lowStart),
                    AbsoluteSample(window, lowEnd), $"RESET {lowUs:F0} µs", "reset");
                continue;
            }

            if (afterReset && lowUs is >= 50 and <= 300)
            {
                yield return new DecoderResult(AbsoluteSample(window, lowStart),
                    AbsoluteSample(window, lowEnd), $"PRESENCE {lowUs:F0} µs", "presence");
                afterReset = false;
                continue;
            }

            if (lowUs is > 0 and < 20)
            {
                var sampleAt = (int)Math.Round(lowStart + 15 * samplesPerMicrosecond,
                    MidpointRounding.AwayFromZero);
                var bit = Level(window, dataChannel, sampleAt) ? 1 : 0;
                if (bits.Count == 0) byteStart = lowStart;
                bits.Add(bit);
                if (bits.Count == 8)
                {
                    var value = 0;
                    for (var b = 0; b < 8; b++) value |= bits[b] << b;
                    yield return new DecoderResult(AbsoluteSample(window, byteStart),
                        AbsoluteSample(window, lowEnd), $"0x{value:X2}", "data");
                    bits.Clear();
                }
            }
            else if (lowUs >= 35)
            {
                if (bits.Count == 0) byteStart = lowStart;
                bits.Add(0);
                if (bits.Count == 8)
                {
                    var value = 0;
                    for (var b = 0; b < 8; b++) value |= bits[b] << b;
                    yield return new DecoderResult(AbsoluteSample(window, byteStart),
                        AbsoluteSample(window, lowEnd), $"0x{value:X2}", "data");
                    bits.Clear();
                }
            }
        }

        if (bits.Count > 0)
            yield return new DecoderResult(AbsoluteSample(window, byteStart),
                window.EndSampleExclusive - 1, $"{string.Join(string.Empty, bits)} (partial)",
                "error", true);
    }
}
