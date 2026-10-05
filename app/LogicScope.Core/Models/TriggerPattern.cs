using System.Globalization;

namespace LogicScope.Core.Models;

/// <summary>A 16-bit logic pattern. Mask bits set to one participate in matching.</summary>
public sealed record TriggerPattern(ushort Value, ushort Mask)
{
    public bool Matches(ushort sample) => (sample & Mask) == (Value & Mask);

    public static TriggerPattern Parse(string expression, string maskText)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(maskText);
        var digits = CleanHex(expression);
        var maskDigits = CleanHex(maskText);
        if (digits.Length is < 1 or > 4)
            throw new FormatException("Pattern value must contain 1–4 hexadecimal/X digits.");
        if (maskDigits.Length is < 1 or > 4 ||
            !ushort.TryParse(maskDigits, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out var explicitMask))
            throw new FormatException("Pattern mask must be a 1–4 digit hexadecimal value.");

        ushort value = 0;
        ushort expressionMask = 0;
        foreach (var character in digits)
        {
            value <<= 4;
            expressionMask <<= 4;
            if (character is 'X' or 'x') continue;
            if (!Uri.IsHexDigit(character))
                throw new FormatException("Pattern value accepts hexadecimal digits and X don't-care nibbles.");
            value |= (ushort)char.GetNumericValue(character);
            expressionMask |= 0xF;
        }

        return new TriggerPattern((ushort)(value & explicitMask),
            (ushort)(expressionMask & explicitMask));
    }

    private static string CleanHex(string text)
    {
        text = text.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
    }
}
