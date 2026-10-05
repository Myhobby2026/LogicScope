using LogicScope.Core.Models;
using Xunit;

namespace LogicScope.Tests;

public sealed class TriggerPatternTests
{
    [Theory]
    [InlineData("A5", "00FF", 0x00A5, 0x00FF)]
    [InlineData("A5XX", "FFFF", 0xA500, 0xFF00)]
    [InlineData("10X5", "FFFF", 0x1005, 0xFF0F)]
    [InlineData("0x1F", "00F0", 0x0010, 0x00F0)]
    public void ParsesHexAndDontCareNibbles(string expression, string maskText,
        int expectedValue, int expectedMask)
    {
        var pattern = TriggerPattern.Parse(expression, maskText);
        Assert.Equal((ushort)expectedValue, pattern.Value);
        Assert.Equal((ushort)expectedMask, pattern.Mask);
    }

    [Fact]
    public void MatchesOnlyMaskedBits()
    {
        var pattern = TriggerPattern.Parse("A5XX", "FFFF");
        Assert.True(pattern.Matches(0xA512));
        Assert.True(pattern.Matches(0xA5EF));
        Assert.False(pattern.Matches(0xA412));
    }

    [Theory]
    [InlineData("", "FFFF")]
    [InlineData("GG", "FFFF")]
    [InlineData("12345", "FFFF")]
    [InlineData("10XX", "ZZZZ")]
    public void RejectsMalformedPatterns(string expression, string mask)
    {
        Assert.Throws<FormatException>(() => TriggerPattern.Parse(expression, mask));
    }
}
