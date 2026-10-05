using LogicScope.Core.Models;

namespace LogicScope.Decode;

public sealed class SpiDecoder(
    int clockChannel,
    int dataOutChannel,
    int? dataInChannel = null,
    int? chipSelectChannel = null,
    bool cpol = false,
    bool cpha = false,
    int wordBits = 8,
    bool threeWire = false,
    bool chipSelectActiveLow = true,
    bool msbFirst = true) : ProtocolDecoder
{
    public override string Name => "SPI";

    protected override IEnumerable<DecoderResult> DecodeCore(SampleWindow window)
    {
        CheckChannel(clockChannel);
        CheckChannel(dataOutChannel);
        if (dataInChannel is { } miso) CheckChannel(miso);
        if (chipSelectChannel is { } chipSelect) CheckChannel(chipSelect);
        if (wordBits is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(wordBits));
        if (!threeWire && dataInChannel is null)
            throw new ArgumentException("Four-wire SPI requires a data-in channel.");

        var previousClock = Level(window, clockChannel, 0);
        var previousCs = chipSelectChannel is { } cs
            ? Level(window, cs, 0)
            : chipSelectActiveLow ? false : true;
        var selected = IsSelected(previousCs);
        var bitCount = 0;
        var outValue = 0u;
        var inValue = 0u;
        var wordStart = 0;

        for (var i = 1; i < window.Samples.Length; i++)
        {
            var clock = Level(window, clockChannel, i);
            var csLevel = chipSelectChannel is { } channel
                ? Level(window, channel, i)
                : previousCs;
            var nowSelected = IsSelected(csLevel);
            if (!selected && nowSelected)
            {
                bitCount = 0;
                outValue = inValue = 0;
                wordStart = i;
            }
            else if (selected && !nowSelected && bitCount != 0)
            {
                yield return new DecoderResult(AbsoluteSample(window, wordStart),
                    AbsoluteSample(window, i), $"partial {bitCount}-bit word", "error", true);
                bitCount = 0;
                outValue = inValue = 0;
            }
            selected = nowSelected;

            if (selected && clock != previousClock)
            {
                var leadingEdge = clock != cpol;
                var sampleOnThisEdge = cpha ? !leadingEdge : leadingEdge;
                if (sampleOnThisEdge)
                {
                    if (bitCount == 0) wordStart = i;
                    outValue = AppendBit(outValue, Level(window, dataOutChannel, i), bitCount);
                    if (dataInChannel is { } input)
                        inValue = AppendBit(inValue, Level(window, input, i), bitCount);
                    bitCount++;
                    if (bitCount == wordBits)
                    {
                        var label = threeWire
                            ? $"IO 0x{outValue:X}"
                            : $"MOSI 0x{outValue:X} / MISO 0x{inValue:X}";
                        yield return new DecoderResult(AbsoluteSample(window, wordStart),
                            AbsoluteSample(window, i), label, "data");
                        bitCount = 0;
                        outValue = inValue = 0;
                    }
                }
            }
            previousClock = clock;
            previousCs = csLevel;
        }
    }

    private bool IsSelected(bool level) => chipSelectChannel is null ||
        (chipSelectActiveLow ? !level : level);

    private uint AppendBit(uint value, bool level, int bitPosition) => msbFirst
        ? (value << 1) | (level ? 1u : 0u)
        : value | ((level ? 1u : 0u) << bitPosition);

    private static void CheckChannel(int channel)
    {
        if ((uint)channel >= 16) throw new ArgumentOutOfRangeException(nameof(channel));
    }
}
