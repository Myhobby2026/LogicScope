using LogicScope.Core.Models;

namespace LogicScope.Decode;

public abstract class ProtocolDecoder
{
    public abstract string Name { get; }

    public IReadOnlyList<DecoderResult> Decode(SampleWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.SampleRateHz == 0 || window.Samples.IsEmpty) return Array.Empty<DecoderResult>();
        return DecodeCore(window)
            .Where(result => result.EndSample >= result.StartSample &&
                             result.EndSample >= window.StartSample &&
                             result.StartSample < window.EndSampleExclusive)
            .OrderBy(result => result.StartSample)
            .ToArray();
    }

    protected abstract IEnumerable<DecoderResult> DecodeCore(SampleWindow window);

    protected static bool Level(SampleWindow window, int channel, int index)
    {
        if ((uint)channel >= 16) throw new ArgumentOutOfRangeException(nameof(channel));
        if ((uint)index >= (uint)window.Samples.Length) return true;
        return (window.Samples.Span[index] & (1 << channel)) != 0;
    }

    protected static long AbsoluteSample(SampleWindow window, double localIndex) =>
        window.StartSample + (long)Math.Round(localIndex, MidpointRounding.AwayFromZero);

    protected static IEnumerable<Transition> Transitions(SampleWindow window, int channel)
    {
        var previous = Level(window, channel, 0);
        for (var i = 1; i < window.Samples.Length; i++)
        {
            var next = Level(window, channel, i);
            if (next != previous) yield return new Transition(i, next);
            previous = next;
        }
    }

    protected readonly record struct Transition(int Index, bool Rising);
}
