using System.Numerics;
using LogicScope.Core.Models;

namespace LogicScope.Decode;

public sealed class Ps2Decoder(int clockChannel, int dataChannel) : ProtocolDecoder
{
    public override string Name => "PS/2";

    protected override IEnumerable<DecoderResult> DecodeCore(SampleWindow window)
    {
        if ((uint)clockChannel >= 16) throw new ArgumentOutOfRangeException(nameof(clockChannel));
        if ((uint)dataChannel >= 16) throw new ArgumentOutOfRangeException(nameof(dataChannel));
        var frame = new List<bool>(11);
        var start = 0;
        var priorClock = Level(window, clockChannel, 0);

        for (var i = 1; i < window.Samples.Length; i++)
        {
            var clock = Level(window, clockChannel, i);
            if (priorClock && !clock)
            {
                var bit = Level(window, dataChannel, i);
                if (frame.Count == 0)
                {
                    if (bit)
                    {
                        priorClock = clock;
                        continue;
                    }
                    start = i;
                }
                frame.Add(bit);
                if (frame.Count == 11)
                {
                    var value = 0;
                    for (var b = 0; b < 8; b++) if (frame[b + 1]) value |= 1 << b;
                    var parityValid = ((BitOperations.PopCount((uint)value) +
                                        (frame[9] ? 1 : 0)) & 1) == 1;
                    var stopValid = frame[10];
                    var error = !parityValid || !stopValid;
                    var text = $"0x{value:X2}{(parityValid ? string.Empty : " parity!")}" +
                               (stopValid ? string.Empty : " stop!");
                    yield return new DecoderResult(AbsoluteSample(window, start),
                        AbsoluteSample(window, i), text, error ? "error" : "data", error);
                    frame.Clear();
                }
            }
            priorClock = clock;
        }

        if (frame.Count > 0)
            yield return new DecoderResult(AbsoluteSample(window, start),
                window.EndSampleExclusive - 1, "truncated PS/2 frame", "error", true);
    }
}
