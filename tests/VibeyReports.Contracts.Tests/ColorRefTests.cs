using System;
using FluentAssertions;
using VibeyReports.Contracts;
using Xunit;

namespace VibeyReports.Contracts.Tests;

public class ColorRefTests
{
    // Pins the COLORREF hypothesis (0x00BBGGRR): red's "R" channel lands in the low byte.
    // If the swatch report shows red/blue swapped, this test (and ColorRef.FromHex/ToHex, the
    // one place the byte order lives) is what to flip.
    [Fact]
    public void FromHex_PureRed_MapsToLowByteUnderTheColorRefHypothesis()
    {
        ColorRef.FromHex("#FF0000").Should().Be(0x000000FFu);
    }

    [Fact]
    public void FromHex_PureGreen_MapsToMiddleByte()
    {
        ColorRef.FromHex("#00FF00").Should().Be(0x0000FF00u);
    }

    [Fact]
    public void FromHex_PureBlue_MapsToHighByteUnderTheColorRefHypothesis()
    {
        ColorRef.FromHex("#0000FF").Should().Be(0x00FF0000u);
    }

    [Theory]
    [InlineData("#000000", 0x00000000u)]
    [InlineData("#FFFFFF", 0x00FFFFFF)]
    [InlineData("#1F2A37", 0x00372A1Fu)]
    public void FromHex_ThenToHex_RoundTrips(string hex, uint expectedColorRef)
    {
        var colorRef = ColorRef.FromHex(hex);

        colorRef.Should().Be(expectedColorRef);
        ColorRef.ToHex(colorRef).Should().Be(hex);
    }

    [Fact]
    public void ToHex_MapsTheMeasuredUnsetSentinelToNull()
    {
        // Measured (this task): every box fill / section background never explicitly set in the
        // Crystal Reports designer reads back as exactly 0xFFFFFFFF, distinct from a real,
        // explicitly-set white (which reads back as 0x00FFFFFF, top byte 0x00).
        ColorRef.ToHex(0xFFFFFFFFu).Should().BeNull();
        ColorRef.ToHex(0x00FFFFFFu).Should().Be("#FFFFFF");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1F2A37")]        // missing '#'
    [InlineData("#1F2A3")]        // too short
    [InlineData("#1F2A377")]      // too long
    [InlineData("#1F2A3G")]       // non-hex character
    [InlineData("#GGGGGG")]       // non-hex characters
    [InlineData(" #1F2A37")]      // leading whitespace
    [InlineData("#1F2A37 ")]      // trailing whitespace
    public void IsValidHex_RejectsAnythingNotExactlyHashPlusSixHexDigits(string? hex)
    {
        ColorRef.IsValidHex(hex).Should().BeFalse();
    }

    [Fact]
    public void IsValidHex_AcceptsUpperAndLowerCaseHexDigits()
    {
        ColorRef.IsValidHex("#abcdef").Should().BeTrue();
        ColorRef.IsValidHex("#ABCDEF").Should().BeTrue();
    }

    [Fact]
    public void FromHex_ThrowsOnInvalidInputRatherThanSilentlyMisparsing()
    {
        Action act = () => ColorRef.FromHex("not-a-colour");
        act.Should().Throw<ArgumentException>();
    }
}
