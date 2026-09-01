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

    // --- VIBEY_DB_PASSWORD ------------------------------------------------------
    //
    // addTable and setTableLocation need a database password (measured: Crystal persists a
    // connection's user name but never its password, so both operations fail "Logon failed"
    // without one). It is supplied through the VIBEY_DB_PASSWORD environment variable and
    // deliberately NOT through the plan - a LayoutOperation field would put a credential into a
    // JSON file on disk.
    //
    // None of the tests below opens a database connection. The "variable not set" path throws
    // before any COM call, and the scrub tests use read/removeTable, which are fully offline. A
    // test that drove a WRONG password to a server rejection would open a real network connection,
    // which with the VPN down BLOCKS rather than fails (post-merge finding PM1) - that path was
    // verified by hand instead and is recorded in docs/sdk-notes.md.

    /// <summary>
    /// Runs the worker with VIBEY_DB_PASSWORD set to <paramref name="password"/> (or removed from
    /// the child's environment when null) and returns the RAW stdout text as well as the parsed
    /// response, because the scrub assertions have to search the serialised JSON, not just the
    /// fields the deserialiser happens to fill.
    /// </summary>
    private static (WorkerResponse Response, string Stdout, string Stderr) RunWithPassword(WorkerRequest request, string password)
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

        // The child's environment only - this never mutates the test process's own environment, so
        // the tests stay safe to run in parallel with everything else in the suite.
        if (password == null) psi.EnvironmentVariables.Remove("VIBEY_DB_PASSWORD");
        else psi.EnvironmentVariables["VIBEY_DB_PASSWORD"] = password;

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(JsonSerializer.Serialize(request, VibeyJson.Options));
        proc.StandardInput.Close();

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);

        proc.ExitCode.Should().Be(0, because: $"worker crashed. stderr: {stderr}");
        return (JsonSerializer.Deserialize<WorkerResponse>(stdout, VibeyJson.Options)!, stdout, stderr);
    }

    private static WorkerRequest AddTableRequest(string dest) => new()
    {
        Command = WorkerCommands.Apply,
        ReportPath = Fixtures.SampleReport,
        OutputPath = dest,
        Plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddTable,
                    Target = "Command",          // SampleReport.rpt's only table
                    TableName = "sp_vibey_probe;1",
                    NewName = "sp_vibey_probe;1"
                }
            }
        }
    };

    /// <summary>
    /// The absent-variable path must name the variable and say what it is for - NOT surface a raw
    /// "Logon failed", which is what shipped before and told the operator nothing actionable.
    /// </summary>
    [Fact]
    public void Apply_AddTableWithoutTheDatabasePasswordVariable_FailsNamingTheVariableNotLogonFailed()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        try
        {
            var (response, _, _) = RunWithPassword(AddTableRequest(dest), password: null);

            response.Ok.Should().BeFalse();
            response.Error.Should().Contain("VIBEY_DB_PASSWORD");
            response.Error.Should().Contain("addTable");
            response.Error.Should().NotContain("Logon failed");
            File.Exists(dest).Should().BeFalse(because: "ok:false must imply nothing was written");
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public void Apply_SetTableLocationWithoutTheDatabasePasswordVariable_FailsNamingTheVariable()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        try
        {
            var request = new WorkerRequest
            {
                Command = WorkerCommands.Apply,
                ReportPath = Fixtures.SampleReport,
                OutputPath = dest,
                Plan = new LayoutPlan
                {
                    Operations =
                    {
                        new LayoutOperation
                        {
                            Action = LayoutActions.SetTableLocation,
                            Target = "Command",
                            TableName = "sp_vibey_probe;1"
                        }
                    }
                }
            };

            var (response, _, _) = RunWithPassword(request, password: null);

            response.Ok.Should().BeFalse();
            response.Error.Should().Contain("VIBEY_DB_PASSWORD");
            response.Error.Should().Contain("setTableLocation");
            response.Error.Should().NotContain("Logon failed");
            File.Exists(dest).Should().BeFalse();
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    /// <summary>
    /// removeTable is fully offline and must stay that way: it neither reads VIBEY_DB_PASSWORD nor
    /// touches a ConnectionInfo, so it succeeds with the variable absent. If it ever grew a
    /// Require() call this test would go red immediately.
    /// </summary>
    [Fact]
    public void Apply_RemoveTableSucceedsWithNoDatabasePasswordSet()
    {
        var read = Run(new WorkerRequest { Command = WorkerCommands.Read, ReportPath = Fixtures.SampleReport });
        var alias = read.Schema!.AvailableFields.Select(f => f.TableAlias).Distinct().Single();
        var bound = read.Schema.Sections.SelectMany(s => s.Objects)
                        .Where(o => o.DataSource != null && o.DataSource.StartsWith("{" + alias + "."))
                        .Select(o => o.Name).ToList();

        var plan = new LayoutPlan();
        foreach (var name in bound)
            plan.Operations.Add(new LayoutOperation { Action = LayoutActions.RemoveObject, Target = name });
        plan.Operations.Add(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = alias });

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        try
        {
            var (response, _, _) = RunWithPassword(
                new WorkerRequest
                {
                    Command = WorkerCommands.Apply,
                    ReportPath = Fixtures.SampleReport,
                    OutputPath = dest,
                    Plan = plan
                },
                password: null);

            response.Ok.Should().BeTrue(because: response.Error);
            response.RemovedTables.Should().ContainSingle().Which.Should().Be(alias);
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    /// <summary>
    /// The SUCCESS path never carries the password out of the process.
    ///
    /// A password unrelated to the response would pass this trivially, so the value used here is
    /// chosen to be one that the response WOULD otherwise contain: the report's own file-name stem,
    /// which appears in schema.reportPath and outputPath. If the final scrub in Program.cs were
    /// removed, the raw value would be all over this JSON and the assertion fails.
    /// </summary>
    [Fact]
    public void Read_SuccessResponseNeverContainsTheDatabasePasswordValue()
    {
        const string secretThatWouldOtherwiseAppear = "SampleReport";

        var (response, stdout, stderr) = RunWithPassword(
            new WorkerRequest { Command = WorkerCommands.Read, ReportPath = Fixtures.SampleReport },
            secretThatWouldOtherwiseAppear);

        response.Ok.Should().BeTrue(because: response.Error);
        stdout.Should().NotContain(secretThatWouldOtherwiseAppear);
        stdout.Should().Contain("***", because: "the value was present and must have been redacted, not merely absent");
        stderr.Should().NotContain(secretThatWouldOtherwiseAppear);
    }

    /// <summary>
    /// The FAILURE path never carries it either. Same technique: the password is set to a string
    /// the error message is guaranteed to quote back (the alias the plan asked for), so the
    /// assertion can only pass because the scrub ran.
    /// </summary>
    [Fact]
    public void Apply_FailureResponseNeverContainsTheDatabasePasswordValue()
    {
        const string secretThatWouldOtherwiseAppear = "sp_definitely_not_in_this_report;1";

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        try
        {
            var (response, stdout, stderr) = RunWithPassword(
                new WorkerRequest
                {
                    Command = WorkerCommands.Apply,
                    ReportPath = Fixtures.SampleReport,
                    OutputPath = dest,
                    Plan = new LayoutPlan
                    {
                        Operations =
                        {
                            new LayoutOperation
                            {
                                Action = LayoutActions.RemoveTable,
                                Target = secretThatWouldOtherwiseAppear
                            }
                        }
                    }
                },
                secretThatWouldOtherwiseAppear);

            response.Ok.Should().BeFalse();
            response.Error.Should().NotBeNullOrEmpty();
            stdout.Should().NotContain(secretThatWouldOtherwiseAppear);
            stdout.Should().Contain("***");
            stderr.Should().NotContain(secretThatWouldOtherwiseAppear);
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }
}
