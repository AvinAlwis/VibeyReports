using System;
using System.Text.RegularExpressions;

namespace VibeyReports.Contracts;

/// <summary>
/// Converts between the "#RRGGBB" hex strings the layout API accepts and the raw UInt32 colour
/// value Crystal's RAS SDK stores on <c>ISCRFontColor.Color</c>, <c>ISCRBoxObject.FillColor</c>/
/// <c>LineColor</c>, <c>ISCRLineObject.LineColor</c> and <c>ISCRSectionFormat.BackgroundColor</c>.
///
/// THE ONE OPEN QUESTION: Crystal's UInt32 could plausibly be either a Win32 COLORREF
/// (0x00BBGGRR, blue in the high byte) or a plain 0x00RRGGBB. This class is built to the
/// COLORREF hypothesis, because Crystal historically follows Win32 GDI conventions and because
/// the measured "no colour" sentinel below (0xFFFFFFFF, not e.g. 0x00000000) matches the
/// CLR_INVALID/"no colour" idiom from that same GDI world. If the swatch report shows red and
/// blue swapped, <see cref="FromHex"/> and <see cref="ToHex"/> are the only two places to fix --
/// swap which channel shifts into bits 0-7 vs bits 16-23.
///
/// MEASURED (this task, by opening Documents.rpt, PMSV10_IndPerfOverview.rpt and
/// SampleReport.rpt read-only and walking every section/box/line with a small standalone probe,
/// not by touching LayoutApplierTests or any suite the "do not run tests" rule covers): every
/// section background and every box fill that was never explicitly set in the Crystal Reports
/// designer reads back as exactly 0xFFFFFFFF, while every colour actually set by the designer --
/// including an explicit white fill -- reads back with its top byte 0x00 (e.g. 0x00FFFFFF for
/// white, 0x00C0C0C0 for silver). 0xFFFFFFFF is therefore treated as "no colour" and mapped to
/// null rather than the nonsensical hex "#FFFFFF" (which would collide with a real, explicitly
/// set white). LineColor and FontColor.Color did not exhibit this sentinel in anything probed --
/// both default to 0 (black) the moment an object exists, matching the brief's own measurement
/// of FontColor.Color's default -- so Unset is only ever actually observed on FillColor and
/// section BackgroundColor, but the mapping is applied uniformly to all four properties since
/// Crystal exposes no separate "is this colour set" flag on any of them.
/// </summary>
public static class ColorRef
{
    /// <summary>Crystal's "no colour set" sentinel, measured on FillColor and section BackgroundColor.</summary>
    public const uint Unset = 0xFFFFFFFFu;

    private static readonly Regex HexPattern = new Regex("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    /// <summary>True if <paramref name="hex"/> matches the required "#RRGGBB" form.</summary>
    public static bool IsValidHex(string? hex) => hex != null && HexPattern.IsMatch(hex);

    /// <summary>
    /// "#RRGGBB" -&gt; Crystal's UInt32 colour value under the COLORREF hypothesis (0x00BBGGRR).
    /// Throws <see cref="ArgumentException"/> if <paramref name="hex"/> does not match "#RRGGBB";
    /// callers on the write path should validate with <see cref="IsValidHex"/> (or go through
    /// LayoutPlanValidator) first so this never has to reject anything in production.
    /// </summary>
    public static uint FromHex(string hex)
    {
        if (!IsValidHex(hex))
            throw new ArgumentException($"\"{hex}\" is not a valid colour; expected the form \"#RRGGBB\".", nameof(hex));

        var r = Convert.ToByte(hex!.Substring(1, 2), 16);
        var g = Convert.ToByte(hex.Substring(3, 2), 16);
        var b = Convert.ToByte(hex.Substring(5, 2), 16);

        return (uint)((b << 16) | (g << 8) | r);
    }

    /// <summary>
    /// Crystal's UInt32 colour value -&gt; "#RRGGBB" under the COLORREF hypothesis, or null when
    /// <paramref name="colorRef"/> is the measured "no colour" sentinel (<see cref="Unset"/>).
    /// </summary>
    public static string? ToHex(uint colorRef)
    {
        if (colorRef == Unset) return null;

        var r = (byte)(colorRef & 0xFF);
        var g = (byte)((colorRef >> 8) & 0xFF);
        var b = (byte)((colorRef >> 16) & 0xFF);

        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
