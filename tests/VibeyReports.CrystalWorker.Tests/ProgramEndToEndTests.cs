using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using VibeyReports.Contracts;
using Xunit;

namespace VibeyReports.CrystalWorker.Tests;

public class ProgramEndToEndTests
{
    private static string WorkerExe
    {
        get
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            var exe = Path.Combine(dir, "VibeyReports.CrystalWorker.exe");
            if (File.Exists(exe)) return exe;
            throw new FileNotFoundException($"Worker exe not found next to the tests: {exe}");
        }
    }

    private static WorkerResponse Run(WorkerRequest request)
    {
        var psi = new ProcessStartInfo(WorkerExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(JsonSerializer.Serialize(request, VibeyJson.Options));
        proc.StandardInput.Close();

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);

        proc.ExitCode.Should().Be(0, because: $"worker crashed. stderr: {stderr}");
        return JsonSerializer.Deserialize<WorkerResponse>(stdout, VibeyJson.Options)!;
    }

    /// <summary>
    /// task-8-supplement.md C1: a StreamReader (used by <see cref="Run"/> above) silently swallows a
    /// leading UTF-8 BOM, so it cannot detect the exact regression the supplement warns about. This
    /// reads stdout as raw bytes via the underlying stream instead, bypassing any decoder that would
    /// hide a BOM.
    /// </summary>
    private static byte[] RunRaw(WorkerRequest request)
    {
        var psi = new ProcessStartInfo(WorkerExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(JsonSerializer.Serialize(request, VibeyJson.Options));
        proc.StandardInput.Close();

        using var buffer = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(buffer);
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);

        proc.ExitCode.Should().Be(0, because: $"worker crashed. stderr: {stderr}");
        return buffer.ToArray();
    }

    [Fact]
    public void Read_ReturnsOkWithASchema()
    {
        var response = Run(new WorkerRequest { Command = WorkerCommands.Read, ReportPath = Fixtures.SampleReport });

        response.Ok.Should().BeTrue(because: response.Error);
        response.Schema.Should().NotBeNull();
        response.Schema!.Sections.Should().NotBeEmpty();
    }

    [Fact]
    public void Apply_WritesTheOutputReportAndReportsTheOperationCount()
    {
        var readResponse = Run(new WorkerRequest { Command = WorkerCommands.Read, ReportPath = Fixtures.SampleReport });
        var firstObject = readResponse.Schema!.Sections
            .SelectMany(s => s.Objects)
            .First(o => o.Kind == "Text" || o.Kind == "Field");

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        try
        {
            var response = Run(new WorkerRequest
            {
                Command = WorkerCommands.Apply,
                ReportPath = Fixtures.SampleReport,
                OutputPath = dest,
                Plan = new LayoutPlan
                {
                    Operations = { new LayoutOperation { Action = LayoutActions.SetBold, Target = firstObject.Name, Bold = true } }
                }
            });

            response.Ok.Should().BeTrue(because: response.Error);
            response.OperationsApplied.Should().Be(1);
            response.OutputPath.Should().Be(Path.GetFullPath(dest));
            File.Exists(dest).Should().BeTrue();
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    /// <summary>
    /// Final review F4: apply_layout's own tool description warns at length that previewing or
    /// continuing from "reportPath" (the source, never modified) instead of "outputPath" makes an
    /// edit look like it had no effect -- and then the response handed back a schema whose
    /// ReportPath field WAS the source path, the exact trap the description warns against.
    /// </summary>
    [Fact]
    public void Apply_ReturnsASchemaWhoseReportPathIsTheOutputFileNotTheSource()
    {
        var readResponse = Run(new WorkerRequest { Command = WorkerCommands.Read, ReportPath = Fixtures.SampleReport });
        var firstObject = readResponse.Schema!.Sections
            .SelectMany(s => s.Objects)
            .First(o => o.Kind == "Text" || o.Kind == "Field");

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        try
        {
            var response = Run(new WorkerRequest
            {
                Command = WorkerCommands.Apply,
                ReportPath = Fixtures.SampleReport,
                OutputPath = dest,
                Plan = new LayoutPlan
                {
                    Operations = { new LayoutOperation { Action = LayoutActions.SetBold, Target = firstObject.Name, Bold = true } }
                }
            });

            response.Ok.Should().BeTrue(because: response.Error);
            response.Schema!.ReportPath.Should().Be(response.OutputPath);
            response.Schema.ReportPath.Should().NotBe(Path.GetFullPath(Fixtures.SampleReport));
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public void Apply_WithAnInvalidPlan_ReturnsOkFalseAndValidationErrorsWithoutCrashing()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");

        var response = Run(new WorkerRequest
        {
            Command = WorkerCommands.Apply,
            ReportPath = Fixtures.SampleReport,
            OutputPath = dest,
            Plan = new LayoutPlan
            {
                Operations = { new LayoutOperation { Action = LayoutActions.Move, Target = "NotReal", LeftTwips = 1, TopTwips = 1 } }
            }
        });

        response.Ok.Should().BeFalse();
        response.ValidationErrors.Should().NotBeNullOrEmpty();
        File.Exists(dest).Should().BeFalse();
    }

    [Fact]
    public void Render_ReturnsBase64Pdf()
    {
        var response = Run(new WorkerRequest { Command = WorkerCommands.Render, ReportPath = Fixtures.SampleReport });

        response.Ok.Should().BeTrue(because: response.Error);
        response.PdfBase64.Should().NotBeNullOrWhiteSpace();
        var bytes = Convert.FromBase64String(response.PdfBase64!);
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
    }

    /// <summary>
    /// task-8-supplement.md C6: Documents.rpt declares parameters with no saved values, so
    /// ReportRenderer.ExportPdf wraps Crystal's COMException in an InvalidOperationException whose
    /// message names the report and explains it can still be read/edited even though it cannot be
    /// previewed. Program's render handler must let that message through to WorkerResponse.Error
    /// verbatim rather than replacing it with something generic, and must still exit 0.
    /// </summary>
    [Fact]
    public void Render_OnAReportWithUnsetParameters_ReturnsOkFalseWithTheActionableMessage()
    {
        var path = Path.Combine(Fixtures.Dir, "Documents.rpt");

        var response = Run(new WorkerRequest { Command = WorkerCommands.Render, ReportPath = path });

        response.Ok.Should().BeFalse();
        response.Error.Should().Contain("Documents.rpt");
        response.Error.Should().Contain("parameter");
    }

    [Fact]
    public void UnknownCommand_ReturnsOkFalseRatherThanCrashing()
    {
        var response = Run(new WorkerRequest { Command = "dropDatabase", ReportPath = Fixtures.SampleReport });

        response.Ok.Should().BeFalse();
        response.Error.Should().Contain("dropDatabase");
    }

    /// <summary>
    /// task-8-supplement.md C1 (Critical), pinned so a regression is caught even though it is
    /// invisible to every string-based test above: Console.OutputEncoding must be a BOM-less
    /// UTF8Encoding, or a leading EF BB BF would make Task 9's JsonSerializer.Deserialize(stdout)
    /// throw "'0xEF' is an invalid start of a value" - indistinguishable from a worker crash across
    /// the process boundary.
    /// </summary>
    [Fact]
    public void Worker_WritesNoByteOrderMarkBeforeItsJson()
    {
        var raw = RunRaw(new WorkerRequest { Command = WorkerCommands.Read, ReportPath = Fixtures.SampleReport });

        raw.Length.Should().BeGreaterThan(0);
        // '{' is 0x7B. A UTF-8 BOM would put 0xEF 0xBB 0xBF first.
        raw[0].Should().Be((byte)'{', because: "stdout must be parseable JSON with no byte-order mark");
    }

    /// <summary>
    /// task-8-findings-round1.md T1: AvailableFields is the allowlist for addField, so a worker
    /// that dropped or truncated it during JSON round-tripping would silently disable that
    /// operation's security boundary while passing every other test.
    /// </summary>
    [Fact]
    public void Read_ReturnsAvailableFieldsThroughTheWorkerProcess()
    {
        var response = Run(new WorkerRequest
        {
            Command = WorkerCommands.Read,
            ReportPath = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt")
        });

        response.Ok.Should().BeTrue(because: response.Error);
        response.Schema!.AvailableFields.Should().NotBeEmpty(
            because: "AvailableFields is the allowlist for addField and must survive JSON round-tripping");
        response.Schema.AvailableFields.Should().OnlyContain(f =>
            !string.IsNullOrWhiteSpace(f.FormulaForm) && f.FormulaForm.StartsWith("{"));
    }

    /// <summary>
    /// task-8-findings-round1.md T2: the only success-path apply test above (
    /// <see cref="Apply_WritesTheOutputReportAndReportsTheOperationCount"/>) uses setBold, which
    /// mutates an existing object. Nothing exercises the add paths end to end through the worker
    /// process -- precisely where F1's post-save read risk lives (a freshly-added object read back
    /// off the same, un-reopened session).
    /// </summary>
    [Fact]
    public void Apply_AddingATextObject_ReturnsTheNewObjectInTheReturnedSchema()
    {
        var reportPath = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");

        var readResponse = Run(new WorkerRequest { Command = WorkerCommands.Read, ReportPath = reportPath });
        readResponse.Ok.Should().BeTrue(because: readResponse.Error);
        var sectionName = readResponse.Schema!.Sections.First(s => s.Kind == "Details" && s.HeightTwips >= 400).Name;

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        try
        {
            var response = Run(new WorkerRequest
            {
                Command = WorkerCommands.Apply,
                ReportPath = reportPath,
                OutputPath = dest,
                Plan = new LayoutPlan
                {
                    Operations =
                    {
                        new LayoutOperation
                        {
                            Action = LayoutActions.AddText, Section = sectionName, NewName = "VibeyAddedText",
                            Text = "Vibey Reports", LeftTwips = 0, TopTwips = 0, WidthTwips = 2880, HeightTwips = 320
                        }
                    }
                }
            });

            response.Ok.Should().BeTrue(because: response.Error);
            response.OperationsApplied.Should().Be(1);
            File.Exists(dest).Should().BeTrue();

            var added = response.Schema!.Sections.SelectMany(s => s.Objects)
                .SingleOrDefault(o => o.Name == "VibeyAddedText");
            added.Should().NotBeNull();
            added!.Kind.Should().Be("Text");
            added.Text.Should().Contain("Vibey Reports");
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    /// <summary>
    /// End-to-end coverage of the twelfth operation through the real worker process: an apply
    /// containing a removeObject returns ok:true with removedObjects populated in the JSON
    /// response, not just OperationsApplied incremented.
    /// </summary>
    [Fact]
    public void Apply_WithARemoveObjectOperation_ReturnsOkTrueWithRemovedObjectsPopulated()
    {
        var readResponse = Run(new WorkerRequest { Command = WorkerCommands.Read, ReportPath = Fixtures.SampleReport });
        var toRemove = readResponse.Schema!.Sections
            .SelectMany(s => s.Objects)
            .First(o => o.Kind == "Text" || o.Kind == "Field").Name;

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        try
        {
            var response = Run(new WorkerRequest
            {
                Command = WorkerCommands.Apply,
                ReportPath = Fixtures.SampleReport,
                OutputPath = dest,
                Plan = new LayoutPlan
                {
                    Operations = { new LayoutOperation { Action = LayoutActions.RemoveObject, Target = toRemove } }
                }
            });

            response.Ok.Should().BeTrue(because: response.Error);
            response.RemovedObjects.Should().NotBeNull();
            response.RemovedObjects.Should().ContainSingle().Which.Should().Be(toRemove);
            response.Schema!.Sections.SelectMany(s => s.Objects).Should().NotContain(o => o.Name == toRemove);
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }
}
