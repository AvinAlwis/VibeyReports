using PDFtoImage;
using SkiaSharp;

namespace VibeyReports.Mcp;

/// <summary>
/// Rasterises a rendered report PDF into a PNG so preview_report can hand Claude something it can
/// actually look at. All Crystal work stays in the worker process; this is pure PDF -> image and
/// never touches a Crystal assembly.
/// </summary>
public static class PdfRasterizer
{
    /// <summary>Renders page 1 of a PDF to PNG bytes. 96 dpi keeps previews small enough to send inline.</summary>
    public static byte[] FirstPageToPng(byte[] pdfBytes, int dpi = 96)
    {
        if (pdfBytes is null || pdfBytes.Length == 0) throw new ArgumentException("PDF bytes are empty.", nameof(pdfBytes));

        using var bitmap = Conversion.ToImage(pdfBytes, page: 0, password: null, options: new RenderOptions(Dpi: dpi));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);

        return data.ToArray();
    }
}
