using PDFtoImage;
using SkiaSharp;

// round-1 fix F4: [SupportedOSPlatform] on a type or member propagates to its callers by design,
// which is why marking just PdfRasterizer (or ReportTools) only pushed the CA1416 warning one call
// site further up the chain instead of resolving it - the chain has to terminate somewhere, and the
// only remaining "caller" past ReportTools is the MCP SDK's reflection, which the analyzer does not
// see. This whole assembly is Windows-only by construction (it launches an x86 .NET Framework COM
// worker), so declaring that once at the assembly level is the correct place to end the chain.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace VibeyReports.Mcp;

/// <summary>
/// Rasterises a rendered report PDF into a PNG so preview_report can hand Claude something it can
/// actually look at. All Crystal work stays in the worker process; this is pure PDF -> image and
/// never touches a Crystal assembly.
/// </summary>
public static class PdfRasterizer
{
    /// <summary>
    /// Renders page 1 of a PDF to PNG bytes.
    /// </summary>
    /// <param name="pdfBytes">The PDF to render.</param>
    /// <param name="dpi">
    /// Render resolution. Callers should clamp this themselves (see ReportTools.PreviewReport,
    /// which restricts the MCP-facing dpi parameter to 72-300) - PDFtoImage's own docs warn that an
    /// oversized target bitmap can produce "corrupted images (e.g. missing text)" rather than an
    /// exception, which is silent data loss for a tool whose entire job is visual inspection.
    /// </param>
    public static byte[] FirstPageToPng(byte[] pdfBytes, int dpi = 96)
    {
        if (pdfBytes is null || pdfBytes.Length == 0) throw new ArgumentException("PDF bytes are empty.", nameof(pdfBytes));

        using var bitmap = Conversion.ToImage(pdfBytes, page: 0, password: null, options: new RenderOptions(Dpi: dpi));

        // round-1 fix F3: the quality argument is meaningless for PNG (a lossless format -
        // SkiaSharp ignores it), but SKBitmap.Encode has no overload without it. Encode can return
        // null (e.g. an unsupported pixel configuration); without this check a direct caller of
        // FirstPageToPng got an unexplained NullReferenceException instead of a diagnosable message.
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp could not encode the rendered page as PNG.");

        return data.ToArray();
    }
}
