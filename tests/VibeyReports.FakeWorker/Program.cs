using System.Text;
using System.Text.Json;

// A deterministic stand-in for the real Crystal worker (round-1 fix T2). Reads one JSON request from
// stdin the same way the real worker does - a BOM-less UTF-8 raw stream read, matching round-1 fix F1
// - and misbehaves on cue based on a keyword in "reportPath", so CrystalWorkerClientTests can exercise
// InvokeAsync's timeout/kill, malformed-JSON, and ExitCode != 0 branches fast and without flakiness.
var bomless = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = bomless;

string raw;
using (var stdin = Console.OpenStandardInput())
using (var reader = new StreamReader(stdin, bomless))
{
    raw = await reader.ReadToEndAsync();
}

var reportPath = "";
try
{
    using var doc = JsonDocument.Parse(raw);
    if (doc.RootElement.TryGetProperty("reportPath", out var el) && el.ValueKind == JsonValueKind.String)
        reportPath = el.GetString() ?? "";
}
catch (JsonException)
{
    // Fall through to the default "answer normally" behaviour below.
}

if (reportPath.Contains("HANG"))
{
    // Sleeps well past any timeout the tests configure; the test kills this process, it never
    // wakes up on its own.
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}

if (reportPath.Contains("GARBAGE"))
{
    Console.Out.Write("not json at all");
    Console.Out.Flush();
    return 0;
}

if (reportPath.Contains("CRASH"))
{
    // Writes nothing to stdout and exits non-zero - the contract's definition of "crashed before
    // it could answer."
    return 3;
}

if (reportPath.Contains("RENDER_OK"))
{
    // round-1 fix T1: a fast, Crystal-free success path for preview_report's ImageContentBlock
    // assertions. Returns the real minimal.pdf fixture as pdfBase64, matching the render command's
    // response shape, so PdfRasterizer runs against real PDF bytes rather than a stub.
    var pdfPath = FindFixture("minimal.pdf");
    var pdfBase64 = Convert.ToBase64String(File.ReadAllBytes(pdfPath));
    Console.Out.Write($"{{\"ok\":true,\"pdfBase64\":\"{pdfBase64}\"}}");
    Console.Out.Flush();
    return 0;
}

Console.Out.Write("{\"ok\":true}");
Console.Out.Flush();
return 0;

// Walks up from this executable's directory looking for tests/fixtures, the same layout
// CrystalWorkerClientTests' Fixtures.Dir relies on.
static string FindFixture(string fileName)
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir, "fixtures", fileName);
        if (File.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }
    throw new FileNotFoundException($"Could not locate tests/fixtures/{fileName} from {AppContext.BaseDirectory}.");
}
