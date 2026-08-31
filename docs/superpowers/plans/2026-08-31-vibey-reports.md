# Vibey Reports Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Claude redesign the visual layout of an existing Crystal Reports XI R2 `.rpt` file and save a new `.rpt` that opens correctly in Crystal Reports XI R2 and in the PeoplesHR software.

**Architecture:** Three projects. `VibeyReports.Contracts` (netstandard2.0) holds the JSON DTOs and the validator that gates every layout change. `VibeyReports.CrystalWorker` (net48, **x86**) is the only code that touches the legacy Crystal XI R2 in-process RAS SDK; it is a console app that reads a JSON request on stdin and writes a JSON response on stdout. `VibeyReports.Mcp` (net10.0) is a stdio MCP server that Claude Code drives directly — it spawns the worker as a child process and returns rendered report previews to Claude as MCP image content. Because Claude Code *is* the client, there is no API key, no web server, and no UI to build.

**Tech Stack:** C#, .NET 10 (MCP server) + .NET Framework 4.8 x86 (Crystal worker), `ModelContextProtocol` 1.2.0, `Microsoft.Extensions.Hosting`, System.Text.Json, xunit 2.9.3 + FluentAssertions 8.9.0, Crystal Reports XI R2 in-process RAS SDK 11.5, PDFtoImage (PDF rasterisation for previews).

**Spec:** [docs/superpowers/specs/2026-08-31-vibey-reports-spec.md](../specs/2026-08-31-vibey-reports-spec.md)

**Project root:** `D:\VibeyReports`

---

## Why this shape (decisions already made)

These were settled before planning. Do not re-litigate them mid-execution.

| Question | Decision | Reason |
|---|---|---|
| What is a "prototype structure"? | One report at a time: open a `.rpt`, give a design instruction, get a redesigned `.rpt`. No cross-report style library in v1. | User's choice. Matches the spec literally. |
| How does Claude reach the tool? | **MCP stdio server**, driven from Claude Code. | User cannot use an API key. Claude Code is already installed and is the "modern, light" interface — no extra runtime to build or run. Matches the existing `tcm-testcases` and `phr-db-mcp` servers on this machine. |
| How does Claude "see" the result? | The `preview_report` MCP tool returns a rendered PNG as MCP **image content**. | Delivers the spec's Phase 6 visual-feedback loop with no vision API and no API key. |
| Why split the worker into its own process? | Crystal 11.5 RAS is .NET 2.0 / x86 / COM-bound. | Keeps the legacy dependency out of the MCP server, exactly as the spec's Phase 7 recommends. Lets the MCP server stay net10.0 like `phr-db-mcp`. |

---

## Global Constraints

Every task's requirements implicitly include this section.

1. **Crystal assemblies come from exactly one place:**
   `C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2\`
   Assembly version `11.5.3300.0`, file version `11.5.9510.1263`, PublicKeyToken `692fbea5521e1304`.

2. **NEVER reference the `CrystalReports.*` 13.x NuGet packages.** `D:\RptToXml` on this machine does exactly that. Those assemblies open XI R2 files but **save them in a newer `.rpt` format that Crystal Reports XI R2 and the PeoplesHR software cannot open.** Using them silently destroys the deliverable. `D:\RptToXml` is useful only as read-side reference code; never copy its `packages.config` or its `<Reference>` block.

3. **`VibeyReports.CrystalWorker` must be `net48` with `<PlatformTarget>x86</PlatformTarget>`.** The Business Objects COM servers are 32-bit only. An AnyCPU or x64 build fails at `CoCreateInstance` with `REGDB_E_CLASSNOTREG` (0x80040154).

4. **All geometry is in twips.** 1 twip = 1/1440 inch. A4 portrait = 11907 × 16838 twips. Letter portrait = 12240 × 15840 twips. Every DTO field carrying a coordinate is suffixed `Twips`.

5. **The writer must refuse to touch:** database connections, SQL commands, table links, formulas, parameters, record selection, grouping, sorting, subreport contents. Layout only. This is enforced in `LayoutPlanValidator`, not by convention.

6. **Never overwrite the input `.rpt`.** `apply_layout` always writes to a new path and fails if the destination already exists unless `overwrite: true` is passed explicitly.

7. **Test corpus:** `D:\RptToXml\RptToXml\Samples\*.rpt` (~40 real reports) plus `D:\PMS_Module\HRM-PMS-NET\docs\solution\8-Reports\PMSV10_IndPerfOverview.rpt`. Task 1 copies a fixed subset into `tests/fixtures/` so tests do not depend on unrelated folders.

8. **Commit after every task.** Conventional Commits (`feat:`, `test:`, `fix:`, `chore:`).

---

## File Structure

```text
D:\VibeyReports\
  VibeyReports.sln
  Directory.Build.props                     # shared: LangVersion, Nullable, warnings
  src\
    VibeyReports.Contracts\                 # netstandard2.0 — no Crystal, no MCP
      ReportSchema.cs                       # ReportSchema, PageInfo, SectionInfo, ObjectInfo
      LayoutPlan.cs                         # LayoutPlan, LayoutOperation (flat + Action discriminator)
      LayoutPlanValidator.cs                # the safety gate
      ValidationResult.cs
      WorkerProtocol.cs                     # WorkerRequest / WorkerResponse envelopes
      Json.cs                               # single shared JsonSerializerOptions
    VibeyReports.CrystalWorker\             # net48, x86 — the ONLY project referencing Crystal
      Program.cs                            # stdin JSON -> stdout JSON dispatch
      CrystalSession.cs                     # open/close ReportClientDocument, IDisposable
      ReportReader.cs                       # .rpt -> ReportSchema
      LayoutApplier.cs                      # LayoutPlan -> RAS mutations
      ReportRenderer.cs                     # .rpt -> PDF bytes
      TwipsGuard.cs                         # bounds checks against page/section
      app.config
    VibeyReports.Mcp\                       # net10.0 — MCP stdio server
      Program.cs                            # host + stdio transport
      CrystalWorkerClient.cs                # spawns worker, JSON in/out, timeouts
      ReportTools.cs                        # [McpServerTool] read_report / apply_layout / preview_report
      PdfRasterizer.cs                       # PDF bytes -> PNG bytes
      WorkerLocator.cs                      # finds CrystalWorker.exe
  tests\
    VibeyReports.Contracts.Tests\           # net10.0 — pure, fast, no Crystal
    VibeyReports.CrystalWorker.Tests\       # net48 x86 — integration, needs Crystal installed
    VibeyReports.Mcp.Tests\                 # net10.0 — worker client + tool wiring
    fixtures\                               # copied .rpt files, committed
  docs\
    superpowers\plans\, superpowers\specs\
    README.md
```

`Contracts` is `netstandard2.0` precisely so both `net48` and `net10.0` can reference the same DTOs — this is what keeps the two runtimes in sync.

---

## Task 1: Solution scaffold, Contracts DTOs, and test fixtures

**Files:**
- Create: `D:\VibeyReports\Directory.Build.props`
- Create: `D:\VibeyReports\src\VibeyReports.Contracts\VibeyReports.Contracts.csproj`
- Create: `D:\VibeyReports\src\VibeyReports.Contracts\ReportSchema.cs`
- Create: `D:\VibeyReports\src\VibeyReports.Contracts\Json.cs`
- Create: `D:\VibeyReports\tests\VibeyReports.Contracts.Tests\VibeyReports.Contracts.Tests.csproj`
- Test: `D:\VibeyReports\tests\VibeyReports.Contracts.Tests\ReportSchemaTests.cs`
- Create: `D:\VibeyReports\tests\fixtures\` (copied `.rpt` files)

**Interfaces:**
- Consumes: nothing.
- Produces: `ReportSchema`, `PageInfo`, `SectionInfo`, `ObjectInfo`, `VibeyJson.Options`. Every later task serialises these.

- [ ] **Step 1: Create the solution and folders**

```bash
cd /d/VibeyReports && dotnet new sln -n VibeyReports && mkdir -p src tests/fixtures
```

- [ ] **Step 2: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create the Contracts project**

```bash
cd /d/VibeyReports && dotnet new classlib -n VibeyReports.Contracts -o src/VibeyReports.Contracts -f netstandard2.0 && rm -f src/VibeyReports.Contracts/Class1.cs
```

Then replace `src/VibeyReports.Contracts/VibeyReports.Contracts.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Text.Json" Version="9.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Copy the test fixtures**

```bash
cd /d/VibeyReports/tests/fixtures && cp "/d/RptToXml/RptToXml/Samples/SampleReport.rpt" . && cp "/d/RptToXml/RptToXml/Samples/Documents.rpt" . && cp "/d/RptToXml/RptToXml/Samples/JournalEntry.rpt" . && cp "/d/PMS_Module/HRM-PMS-NET/docs/solution/8-Reports/PMSV10_IndPerfOverview.rpt" . && ls -la
```

- [ ] **Step 5: Write the failing test**

`tests/VibeyReports.Contracts.Tests/ReportSchemaTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using VibeyReports.Contracts;
using Xunit;

namespace VibeyReports.Contracts.Tests;

public class ReportSchemaTests
{
    [Fact]
    public void ReportSchema_RoundTripsThroughJson_PreservingGeometryAndFonts()
    {
        var schema = new ReportSchema
        {
            ReportPath = @"C:\reports\demo.rpt",
            Page = new PageInfo
            {
                WidthTwips = 12240,
                HeightTwips = 15840,
                MarginLeftTwips = 720,
                MarginRightTwips = 720,
                MarginTopTwips = 720,
                MarginBottomTwips = 720,
                Orientation = "Portrait"
            },
            Sections =
            {
                new SectionInfo
                {
                    Name = "Section3",
                    Kind = "Details",
                    HeightTwips = 400,
                    Suppressed = false,
                    Objects =
                    {
                        new ObjectInfo
                        {
                            Name = "CustomerName",
                            Kind = "Field",
                            LeftTwips = 300,
                            TopTwips = 20,
                            WidthTwips = 2500,
                            HeightTwips = 300,
                            FontName = "Arial",
                            FontSizePt = 10f,
                            Bold = false,
                            Italic = false,
                            Underline = false,
                            Alignment = "Left",
                            DataSource = "{Customer.Name}",
                            Text = null
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(schema, VibeyJson.Options);
        var back = JsonSerializer.Deserialize<ReportSchema>(json, VibeyJson.Options)!;

        back.Page.WidthTwips.Should().Be(12240);
        back.Sections.Should().HaveCount(1);
        back.Sections[0].Objects[0].DataSource.Should().Be("{Customer.Name}");
        back.Sections[0].Objects[0].FontSizePt.Should().Be(10f);
    }

    [Fact]
    public void ReportSchema_SerialisesWithCamelCaseNames()
    {
        var schema = new ReportSchema { ReportPath = "x.rpt" };
        var json = JsonSerializer.Serialize(schema, VibeyJson.Options);

        json.Should().Contain("\"reportPath\"");
        json.Should().NotContain("\"ReportPath\"");
    }
}
```

- [ ] **Step 6: Create the test project and run the test to verify it fails**

```bash
cd /d/VibeyReports && dotnet new xunit -n VibeyReports.Contracts.Tests -o tests/VibeyReports.Contracts.Tests -f net10.0 && rm -f tests/VibeyReports.Contracts.Tests/UnitTest1.cs && dotnet add tests/VibeyReports.Contracts.Tests package FluentAssertions --version 8.9.0 && dotnet add tests/VibeyReports.Contracts.Tests reference src/VibeyReports.Contracts && dotnet sln add src/VibeyReports.Contracts tests/VibeyReports.Contracts.Tests
```

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Contracts.Tests
```

Expected: FAIL — `The type or namespace name 'ReportSchema' could not be found`.

- [ ] **Step 7: Write `Json.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeyReports.Contracts;

public static class VibeyJson
{
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}
```

- [ ] **Step 8: Write `ReportSchema.cs`**

```csharp
using System.Collections.Generic;

namespace VibeyReports.Contracts;

public sealed class ReportSchema
{
    public string ReportPath { get; set; } = "";
    public PageInfo Page { get; set; } = new PageInfo();
    public List<SectionInfo> Sections { get; set; } = new List<SectionInfo>();
}

public sealed class PageInfo
{
    public int WidthTwips { get; set; }
    public int HeightTwips { get; set; }
    public int MarginLeftTwips { get; set; }
    public int MarginRightTwips { get; set; }
    public int MarginTopTwips { get; set; }
    public int MarginBottomTwips { get; set; }
    /// <summary>"Portrait" or "Landscape".</summary>
    public string Orientation { get; set; } = "Portrait";
}

public sealed class SectionInfo
{
    /// <summary>RAS section name, e.g. "Section3". This is the stable identifier.</summary>
    public string Name { get; set; } = "";
    /// <summary>Human-readable band: ReportHeader, PageHeader, GroupHeader, Details, GroupFooter, ReportFooter, PageFooter.</summary>
    public string Kind { get; set; } = "";
    public int HeightTwips { get; set; }
    public bool Suppressed { get; set; }
    public List<ObjectInfo> Objects { get; set; } = new List<ObjectInfo>();
}

public sealed class ObjectInfo
{
    /// <summary>RAS object name. Unique within the report. Used as the operation target.</summary>
    public string Name { get; set; } = "";
    /// <summary>Field, Text, Line, Box, Subreport, Picture, Chart, Crosstab, FieldHeading, Other.</summary>
    public string Kind { get; set; } = "";
    public int LeftTwips { get; set; }
    public int TopTwips { get; set; }
    public int WidthTwips { get; set; }
    public int HeightTwips { get; set; }

    public string? FontName { get; set; }
    public float? FontSizePt { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }
    /// <summary>Left, Right, Centre, Justified, Default.</summary>
    public string? Alignment { get; set; }

    /// <summary>Formula/field expression for Field objects, e.g. "{Customer.Name}". Read-only to the AI.</summary>
    public string? DataSource { get; set; }
    /// <summary>Literal text for Text objects.</summary>
    public string? Text { get; set; }
}
```

- [ ] **Step 9: Run the tests to verify they pass**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Contracts.Tests
```

Expected: PASS, 2 tests.

- [ ] **Step 10: Commit**

```bash
cd /d/VibeyReports && git init -q 2>/dev/null; git add -A && git commit -m "feat: scaffold solution with ReportSchema contracts and test fixtures"
```

---

## Task 2: LayoutPlan and the operation model

**Files:**
- Create: `src/VibeyReports.Contracts/LayoutPlan.cs`
- Test: `tests/VibeyReports.Contracts.Tests/LayoutPlanTests.cs`

**Interfaces:**
- Consumes: `VibeyJson.Options` from Task 1.
- Produces: `LayoutPlan` (with `PlanVersion`, `Operations`), `LayoutOperation` (flat DTO with `Action` discriminator and nullable payload fields), `LayoutActions` (string constants). Tasks 3, 6 and 10 all bind to these exact names.

**Design note:** `LayoutOperation` is deliberately one flat class with nullable fields rather than a polymorphic hierarchy. Reasons: `netstandard2.0` has no `[JsonPolymorphic]`; a flat shape is far easier for a language model to emit correctly; and it forces all "which fields are required for which action" logic into `LayoutPlanValidator` (Task 3), which is the single safety gate.

- [ ] **Step 1: Write the failing test**

`tests/VibeyReports.Contracts.Tests/LayoutPlanTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using VibeyReports.Contracts;
using Xunit;

namespace VibeyReports.Contracts.Tests;

public class LayoutPlanTests
{
    [Fact]
    public void LayoutPlan_DeserialisesTheSpecExampleOperation()
    {
        const string json = """
        {
          "planVersion": 1,
          "operations": [
            { "action": "move", "target": "CustomerName", "leftTwips": 400, "topTwips": 50 }
          ]
        }
        """;

        var plan = JsonSerializer.Deserialize<LayoutPlan>(json, VibeyJson.Options)!;

        plan.PlanVersion.Should().Be(1);
        plan.Operations.Should().HaveCount(1);
        plan.Operations[0].Action.Should().Be(LayoutActions.Move);
        plan.Operations[0].Target.Should().Be("CustomerName");
        plan.Operations[0].LeftTwips.Should().Be(400);
        plan.Operations[0].TopTwips.Should().Be(50);
        plan.Operations[0].WidthTwips.Should().BeNull();
    }

    [Fact]
    public void LayoutPlan_DeserialisesAllTenMvpActions()
    {
        const string json = """
        {
          "planVersion": 1,
          "operations": [
            { "action": "move",          "target": "A", "leftTwips": 1, "topTwips": 2 },
            { "action": "resize",        "target": "A", "widthTwips": 3, "heightTwips": 4 },
            { "action": "setFont",       "target": "A", "fontName": "Calibri" },
            { "action": "setFontSize",   "target": "A", "fontSizePt": 11.5 },
            { "action": "setBold",       "target": "A", "bold": true },
            { "action": "setAlignment",  "target": "A", "alignment": "Centre" },
            { "action": "addText",       "section": "Section1", "newName": "Title", "text": "Payroll",
              "leftTwips": 0, "topTwips": 0, "widthTwips": 5000, "heightTwips": 320 },
            { "action": "addLine",       "section": "Section1", "newName": "Rule",
              "leftTwips": 0, "topTwips": 340, "widthTwips": 5000, "heightTwips": 0 },
            { "action": "addBox",        "section": "Section1", "newName": "Frame",
              "leftTwips": 0, "topTwips": 0, "widthTwips": 5000, "heightTwips": 400 },
            { "action": "resizeSection", "section": "Section3", "heightTwips": 600 }
          ]
        }
        """;

        var plan = JsonSerializer.Deserialize<LayoutPlan>(json, VibeyJson.Options)!;

        plan.Operations.Should().HaveCount(10);
        plan.Operations[3].FontSizePt.Should().Be(11.5f);
        plan.Operations[4].Bold.Should().BeTrue();
        plan.Operations[6].Text.Should().Be("Payroll");
        plan.Operations[9].Section.Should().Be("Section3");
    }

    [Fact]
    public void LayoutActions_All_ContainsExactlyTheTenMvpActions()
    {
        LayoutActions.All.Should().BeEquivalentTo(new[]
        {
            "move", "resize", "setFont", "setFontSize", "setBold",
            "setAlignment", "addText", "addLine", "addBox", "resizeSection"
        });
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Contracts.Tests --filter LayoutPlanTests
```

Expected: FAIL — `The type or namespace name 'LayoutPlan' could not be found`.

- [ ] **Step 3: Write `LayoutPlan.cs`**

```csharp
using System.Collections.Generic;

namespace VibeyReports.Contracts;

public static class LayoutActions
{
    public const string Move = "move";
    public const string Resize = "resize";
    public const string SetFont = "setFont";
    public const string SetFontSize = "setFontSize";
    public const string SetBold = "setBold";
    public const string SetAlignment = "setAlignment";
    public const string AddText = "addText";
    public const string AddLine = "addLine";
    public const string AddBox = "addBox";
    public const string ResizeSection = "resizeSection";

    public static readonly string[] All =
    {
        Move, Resize, SetFont, SetFontSize, SetBold,
        SetAlignment, AddText, AddLine, AddBox, ResizeSection
    };
}

public sealed class LayoutPlan
{
    public int PlanVersion { get; set; } = 1;
    public List<LayoutOperation> Operations { get; set; } = new List<LayoutOperation>();
}

/// <summary>
/// One layout change. Flat by design: which fields are required depends on
/// <see cref="Action"/>, and that rule lives in LayoutPlanValidator.
/// </summary>
public sealed class LayoutOperation
{
    public string Action { get; set; } = "";

    /// <summary>Existing object name. Required for move/resize/setFont/setFontSize/setBold/setAlignment.</summary>
    public string? Target { get; set; }

    /// <summary>Section name. Required for addText/addLine/addBox/resizeSection.</summary>
    public string? Section { get; set; }

    /// <summary>Name to give a newly created object. Required for addText/addLine/addBox.</summary>
    public string? NewName { get; set; }

    public int? LeftTwips { get; set; }
    public int? TopTwips { get; set; }
    public int? WidthTwips { get; set; }
    public int? HeightTwips { get; set; }

    public string? FontName { get; set; }
    public float? FontSizePt { get; set; }
    public bool? Bold { get; set; }
    /// <summary>Left, Right, Centre, Justified.</summary>
    public string? Alignment { get; set; }

    /// <summary>Literal text for addText.</summary>
    public string? Text { get; set; }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Contracts.Tests
```

Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: add LayoutPlan operation model with ten MVP actions"
```

---

## Task 3: LayoutPlanValidator — the safety gate

**Files:**
- Create: `src/VibeyReports.Contracts/ValidationResult.cs`
- Create: `src/VibeyReports.Contracts/LayoutPlanValidator.cs`
- Test: `tests/VibeyReports.Contracts.Tests/LayoutPlanValidatorTests.cs`

**Interfaces:**
- Consumes: `ReportSchema`, `LayoutPlan`, `LayoutOperation`, `LayoutActions`.
- Produces: `ValidationResult` (`IsValid`, `Errors`), `ValidationError` (`OperationIndex`, `Message`), and `LayoutPlanValidator.Validate(LayoutPlan plan, ReportSchema schema) -> ValidationResult`. Tasks 6 and 10 call this exact signature.

This is the single most important task in the plan. Every constraint from Global Constraint 5 is enforced here, and Task 6 is allowed to assume its input is already valid.

- [ ] **Step 1: Write the failing test**

`tests/VibeyReports.Contracts.Tests/LayoutPlanValidatorTests.cs`:

```csharp
using FluentAssertions;
using VibeyReports.Contracts;
using Xunit;

namespace VibeyReports.Contracts.Tests;

public class LayoutPlanValidatorTests
{
    private static ReportSchema Schema() => new ReportSchema
    {
        ReportPath = "demo.rpt",
        Page = new PageInfo
        {
            WidthTwips = 12240, HeightTwips = 15840,
            MarginLeftTwips = 720, MarginRightTwips = 720,
            MarginTopTwips = 720, MarginBottomTwips = 720,
            Orientation = "Portrait"
        },
        Sections =
        {
            new SectionInfo
            {
                Name = "Section1", Kind = "ReportHeader", HeightTwips = 800,
                Objects = { new ObjectInfo { Name = "Title", Kind = "Text", LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 300 } }
            },
            new SectionInfo
            {
                Name = "Section3", Kind = "Details", HeightTwips = 400,
                Objects = { new ObjectInfo { Name = "CustomerName", Kind = "Field", LeftTwips = 300, TopTwips = 20, WidthTwips = 2500, HeightTwips = 300, DataSource = "{Customer.Name}" } }
            }
        }
    };

    private static LayoutPlan PlanOf(params LayoutOperation[] ops) =>
        new LayoutPlan { PlanVersion = 1, Operations = { } }.With(ops);

    [Fact]
    public void Validate_AcceptsAWellFormedMoveWithinPageWidth()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.Move, Target = "CustomerName", LeftTwips = 400, TopTwips = 50
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsUnknownAction()
    {
        var plan = PlanOf(new LayoutOperation { Action = "setDatabaseConnection", Target = "CustomerName" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
              .Which.Message.Should().Contain("not a supported action");
    }

    [Fact]
    public void Validate_RejectsTargetThatDoesNotExistInTheReport()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.Move, Target = "NoSuchObject", LeftTwips = 10, TopTwips = 10
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("NoSuchObject");
    }

    [Fact]
    public void Validate_RejectsMoveMissingCoordinates()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.Move, Target = "CustomerName" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("leftTwips");
    }

    [Fact]
    public void Validate_RejectsObjectPushedPastThePrintableWidth()
    {
        // printable width = 12240 - 720 - 720 = 10800. Object is 2500 wide.
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.Move, Target = "CustomerName", LeftTwips = 9000, TopTwips = 0
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("printable width");
    }

    [Fact]
    public void Validate_RejectsObjectTallerThanItsSection()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.Resize, Target = "CustomerName", WidthTwips = 2500, HeightTwips = 900
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("section \"Section3\"");
    }

    [Fact]
    public void Validate_RejectsNegativeAndZeroSizes()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.Resize, Target = "CustomerName", WidthTwips = 0, HeightTwips = -5
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_RejectsAddTextIntoAnUnknownSection()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddText, Section = "SectionZ", NewName = "New1", Text = "hi",
            LeftTwips = 0, TopTwips = 0, WidthTwips = 100, HeightTwips = 100
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("SectionZ");
    }

    [Fact]
    public void Validate_RejectsAddTextWhoseNewNameCollidesWithAnExistingObject()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddText, Section = "Section1", NewName = "Title", Text = "hi",
            LeftTwips = 0, TopTwips = 0, WidthTwips = 100, HeightTwips = 100
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("already exists");
    }

    [Fact]
    public void Validate_RejectsUnknownAlignmentValue()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetAlignment, Target = "Title", Alignment = "Diagonal"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("Diagonal");
    }

    [Fact]
    public void Validate_RejectsAbsurdFontSizes()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetFontSize, Target = "Title", FontSizePt = 400f
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("between 4 and 72");
    }

    [Fact]
    public void Validate_RejectsResizeSectionBeyondPageHeight()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.ResizeSection, Section = "Section3", HeightTwips = 20000
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("printable height");
    }

    [Fact]
    public void Validate_ReportsEveryBadOperationNotJustTheFirst()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = "bogus" },
            new LayoutOperation { Action = LayoutActions.Move, Target = "NoSuchObject", LeftTwips = 1, TopTwips = 1 });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.Errors.Should().HaveCount(2);
        result.Errors[0].OperationIndex.Should().Be(0);
        result.Errors[1].OperationIndex.Should().Be(1);
    }

    [Fact]
    public void Validate_AccountsForEarlierOperationsWhenCheckingLaterOnes()
    {
        // Grow the section first, then place a tall object that only fits afterwards.
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.ResizeSection, Section = "Section3", HeightTwips = 1200 },
            new LayoutOperation { Action = LayoutActions.Resize, Target = "CustomerName", WidthTwips = 2500, HeightTwips = 1100 });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }
}

internal static class PlanExtensions
{
    public static LayoutPlan With(this LayoutPlan plan, params LayoutOperation[] ops)
    {
        plan.Operations.AddRange(ops);
        return plan;
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Contracts.Tests --filter LayoutPlanValidatorTests
```

Expected: FAIL — `The name 'LayoutPlanValidator' does not exist`.

- [ ] **Step 3: Write `ValidationResult.cs`**

```csharp
using System.Collections.Generic;

namespace VibeyReports.Contracts;

public sealed class ValidationError
{
    public int OperationIndex { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ValidationResult
{
    public bool IsValid { get; set; }
    public List<ValidationError> Errors { get; set; } = new List<ValidationError>();

    public static ValidationResult Ok() => new ValidationResult { IsValid = true };
}
```

- [ ] **Step 4: Write `LayoutPlanValidator.cs`**

The validator simulates the plan against a working copy of the schema so that operation N is checked against the state left by operations 0..N-1. That is what makes the "grow the section, then grow the object" case pass.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace VibeyReports.Contracts;

public static class LayoutPlanValidator
{
    private const float MinFontPt = 4f;
    private const float MaxFontPt = 72f;

    private static readonly HashSet<string> Alignments =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Left", "Right", "Centre", "Center", "Justified" };

    public static ValidationResult Validate(LayoutPlan plan, ReportSchema schema)
    {
        var result = new ValidationResult { IsValid = true };
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (schema == null) throw new ArgumentNullException(nameof(schema));

        // Mutable simulation state: sectionName -> height, objectName -> (section, l, t, w, h)
        var sectionHeights = schema.Sections.ToDictionary(s => s.Name, s => s.HeightTwips, StringComparer.OrdinalIgnoreCase);
        var objects = new Dictionary<string, SimObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in schema.Sections)
            foreach (var o in s.Objects)
                objects[o.Name] = new SimObject
                {
                    Section = s.Name, Left = o.LeftTwips, Top = o.TopTwips,
                    Width = o.WidthTwips, Height = o.HeightTwips
                };

        var printableWidth = schema.Page.WidthTwips - schema.Page.MarginLeftTwips - schema.Page.MarginRightTwips;
        var printableHeight = schema.Page.HeightTwips - schema.Page.MarginTopTwips - schema.Page.MarginBottomTwips;

        for (var i = 0; i < plan.Operations.Count; i++)
        {
            var op = plan.Operations[i];
            var errors = ValidateOne(op, i, objects, sectionHeights, printableWidth, printableHeight);
            result.Errors.AddRange(errors);
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    private static List<ValidationError> ValidateOne(
        LayoutOperation op, int index,
        Dictionary<string, SimObject> objects,
        Dictionary<string, int> sectionHeights,
        int printableWidth, int printableHeight)
    {
        var errs = new List<ValidationError>();
        void Err(string m) => errs.Add(new ValidationError { OperationIndex = index, Message = m });

        var action = op.Action ?? "";
        if (!LayoutActions.All.Contains(action, StringComparer.Ordinal))
        {
            Err($"\"{action}\" is not a supported action. Supported: {string.Join(", ", LayoutActions.All)}.");
            return errs;
        }

        var needsTarget = action is LayoutActions.Move or LayoutActions.Resize or LayoutActions.SetFont
            or LayoutActions.SetFontSize or LayoutActions.SetBold or LayoutActions.SetAlignment;
        var needsSection = action is LayoutActions.AddText or LayoutActions.AddLine
            or LayoutActions.AddBox or LayoutActions.ResizeSection;

        SimObject? target = null;
        if (needsTarget)
        {
            if (string.IsNullOrWhiteSpace(op.Target)) { Err($"\"{action}\" requires \"target\"."); return errs; }
            if (!objects.TryGetValue(op.Target!, out target))
            {
                Err($"Object \"{op.Target}\" does not exist in the report.");
                return errs;
            }
        }

        if (needsSection)
        {
            if (string.IsNullOrWhiteSpace(op.Section)) { Err($"\"{action}\" requires \"section\"."); return errs; }
            if (!sectionHeights.ContainsKey(op.Section!))
            {
                Err($"Section \"{op.Section}\" does not exist in the report.");
                return errs;
            }
        }

        switch (action)
        {
            case LayoutActions.Move:
            {
                if (op.LeftTwips is null) Err("\"move\" requires \"leftTwips\".");
                if (op.TopTwips is null) Err("\"move\" requires \"topTwips\".");
                if (errs.Count > 0) return errs;

                var left = op.LeftTwips!.Value;
                var top = op.TopTwips!.Value;
                if (left < 0) Err("\"leftTwips\" must not be negative.");
                if (top < 0) Err("\"topTwips\" must not be negative.");
                if (left + target!.Width > printableWidth)
                    Err($"Moving \"{op.Target}\" to leftTwips {left} puts its right edge at {left + target.Width}, past the printable width of {printableWidth}.");
                if (top + target.Height > sectionHeights[target.Section])
                    Err($"Moving \"{op.Target}\" to topTwips {top} puts its bottom edge at {top + target.Height}, past the height of section \"{target.Section}\" ({sectionHeights[target.Section]}).");
                if (errs.Count == 0) { target.Left = left; target.Top = top; }
                break;
            }

            case LayoutActions.Resize:
            {
                if (op.WidthTwips is null) Err("\"resize\" requires \"widthTwips\".");
                if (op.HeightTwips is null) Err("\"resize\" requires \"heightTwips\".");
                if (errs.Count > 0) return errs;

                var w = op.WidthTwips!.Value;
                var h = op.HeightTwips!.Value;
                if (w <= 0) Err("\"widthTwips\" must be greater than zero.");
                if (h < 0) Err("\"heightTwips\" must not be negative.");
                if (errs.Count > 0) return errs;

                if (target!.Left + w > printableWidth)
                    Err($"Resizing \"{op.Target}\" to width {w} puts its right edge at {target.Left + w}, past the printable width of {printableWidth}.");
                if (target.Top + h > sectionHeights[target.Section])
                    Err($"Resizing \"{op.Target}\" to height {h} puts its bottom edge at {target.Top + h}, past the height of section \"{target.Section}\" ({sectionHeights[target.Section]}).");
                if (errs.Count == 0) { target.Width = w; target.Height = h; }
                break;
            }

            case LayoutActions.SetFont:
                if (string.IsNullOrWhiteSpace(op.FontName)) Err("\"setFont\" requires a non-empty \"fontName\".");
                break;

            case LayoutActions.SetFontSize:
                if (op.FontSizePt is null) Err("\"setFontSize\" requires \"fontSizePt\".");
                else if (op.FontSizePt < MinFontPt || op.FontSizePt > MaxFontPt)
                    Err($"\"fontSizePt\" must be between {MinFontPt} and {MaxFontPt}; got {op.FontSizePt}.");
                break;

            case LayoutActions.SetBold:
                if (op.Bold is null) Err("\"setBold\" requires \"bold\".");
                break;

            case LayoutActions.SetAlignment:
                if (string.IsNullOrWhiteSpace(op.Alignment)) Err("\"setAlignment\" requires \"alignment\".");
                else if (!Alignments.Contains(op.Alignment!))
                    Err($"\"{op.Alignment}\" is not a valid alignment. Use Left, Right, Centre or Justified.");
                break;

            case LayoutActions.AddText:
            case LayoutActions.AddLine:
            case LayoutActions.AddBox:
            {
                if (string.IsNullOrWhiteSpace(op.NewName)) { Err($"\"{action}\" requires \"newName\"."); return errs; }
                if (objects.ContainsKey(op.NewName!)) { Err($"An object named \"{op.NewName}\" already exists in the report."); return errs; }
                if (action == LayoutActions.AddText && string.IsNullOrEmpty(op.Text)) Err("\"addText\" requires \"text\".");

                if (op.LeftTwips is null || op.TopTwips is null || op.WidthTwips is null || op.HeightTwips is null)
                { Err($"\"{action}\" requires leftTwips, topTwips, widthTwips and heightTwips."); return errs; }

                var l = op.LeftTwips.Value; var t = op.TopTwips.Value;
                var w = op.WidthTwips.Value; var h = op.HeightTwips.Value;
                if (l < 0 || t < 0) Err("Coordinates must not be negative.");
                if (w < 0 || h < 0) Err("Sizes must not be negative.");
                if (errs.Count > 0) return errs;

                if (l + w > printableWidth)
                    Err($"\"{op.NewName}\" would end at {l + w}, past the printable width of {printableWidth}.");
                if (t + h > sectionHeights[op.Section!])
                    Err($"\"{op.NewName}\" would end at {t + h}, past the height of section \"{op.Section}\" ({sectionHeights[op.Section!]}).");

                if (errs.Count == 0)
                    objects[op.NewName!] = new SimObject { Section = op.Section!, Left = l, Top = t, Width = w, Height = h };
                break;
            }

            case LayoutActions.ResizeSection:
            {
                if (op.HeightTwips is null) { Err("\"resizeSection\" requires \"heightTwips\"."); return errs; }
                var h = op.HeightTwips.Value;
                if (h < 0) { Err("\"heightTwips\" must not be negative."); return errs; }
                if (h > printableHeight)
                    Err($"Section height {h} exceeds the printable height of {printableHeight}.");
                else
                {
                    // Shrinking must not orphan an object that is already placed lower down.
                    foreach (var kv in objects.Where(k => string.Equals(k.Value.Section, op.Section, StringComparison.OrdinalIgnoreCase)))
                        if (kv.Value.Top + kv.Value.Height > h)
                            Err($"Shrinking section \"{op.Section}\" to {h} would clip \"{kv.Key}\", which ends at {kv.Value.Top + kv.Value.Height}.");

                    if (errs.Count == 0) sectionHeights[op.Section!] = h;
                }
                break;
            }
        }

        return errs;
    }

    private sealed class SimObject
    {
        public string Section = "";
        public int Left, Top, Width, Height;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Contracts.Tests
```

Expected: PASS, 19 tests.

- [ ] **Step 6: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: add LayoutPlanValidator enforcing layout-only changes and page bounds"
```

---

## Task 4: CrystalWorker project and SDK binding smoke test

**Files:**
- Create: `src/VibeyReports.CrystalWorker/VibeyReports.CrystalWorker.csproj`
- Create: `src/VibeyReports.CrystalWorker/app.config`
- Create: `src/VibeyReports.CrystalWorker/CrystalSession.cs`
- Test: `tests/VibeyReports.CrystalWorker.Tests/VibeyReports.CrystalWorker.Tests.csproj`
- Test: `tests/VibeyReports.CrystalWorker.Tests/CrystalSessionTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `CrystalSession` — `CrystalSession.Open(string rptPath) -> CrystalSession`, property `ISCDReportClientDocument Document { get; }`, `void SaveAs(string destinationPath, bool overwrite)`, `IDisposable`. Tasks 5, 6 and 7 all build on it.

**This task exists to fail loudly and early if the SDK binding is wrong.** x86, COM registration, and .NET 2.0 assembly loading are the three things most likely to break, and they break at the very first `new ReportClientDocumentClass()`. Do not proceed to Task 5 until this passes.

- [ ] **Step 1: Create the worker project**

```bash
cd /d/VibeyReports && mkdir -p src/VibeyReports.CrystalWorker
```

Write `src/VibeyReports.CrystalWorker/VibeyReports.CrystalWorker.csproj`. Note `RestorePackages`/SDK-style targeting `net48` with explicit `HintPath` references to the **11.5** assemblies, and `Private=true` so they are copied next to the exe:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <PlatformTarget>x86</PlatformTarget>
    <Nullable>disable</Nullable>
    <AssemblyName>VibeyReports.CrystalWorker</AssemblyName>
    <RootNamespace>VibeyReports.CrystalWorker</RootNamespace>
    <CrystalDir>C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2</CrystalDir>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.Text.Json" Version="9.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\VibeyReports.Contracts\VibeyReports.Contracts.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="CrystalDecisions.ReportAppServer.ClientDoc">
      <HintPath>$(CrystalDir)\CrystalDecisions.ReportAppServer.ClientDoc.dll</HintPath>
      <Private>true</Private>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </Reference>
    <Reference Include="CrystalDecisions.ReportAppServer.Controllers">
      <HintPath>$(CrystalDir)\CrystalDecisions.ReportAppServer.Controllers.dll</HintPath>
      <Private>true</Private>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </Reference>
    <Reference Include="CrystalDecisions.ReportAppServer.ReportDefModel">
      <HintPath>$(CrystalDir)\CrystalDecisions.ReportAppServer.ReportDefModel.dll</HintPath>
      <Private>true</Private>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </Reference>
    <Reference Include="CrystalDecisions.ReportAppServer.CommonObjectModel">
      <HintPath>$(CrystalDir)\CrystalDecisions.ReportAppServer.CommonObjectModel.dll</HintPath>
      <Private>true</Private>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </Reference>
    <Reference Include="CrystalDecisions.ReportAppServer.DataDefModel">
      <HintPath>$(CrystalDir)\CrystalDecisions.ReportAppServer.DataDefModel.dll</HintPath>
      <Private>true</Private>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </Reference>
    <Reference Include="CrystalDecisions.ReportAppServer.CommLayer">
      <HintPath>$(CrystalDir)\CrystalDecisions.ReportAppServer.CommLayer.dll</HintPath>
      <Private>true</Private>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </Reference>
    <Reference Include="CrystalDecisions.ReportAppServer.ObjectFactory">
      <HintPath>$(CrystalDir)\CrystalDecisions.ReportAppServer.ObjectFactory.dll</HintPath>
      <Private>true</Private>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </Reference>
    <Reference Include="System.Drawing" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write `app.config`**

`useLegacyV2RuntimeActivationPolicy` is required because these are CLR 2.0 COM interop assemblies being loaded by the CLR 4 runtime.

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup useLegacyV2RuntimeActivationPolicy="true">
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
    <supportedRuntime version="v2.0.50727" />
  </startup>
</configuration>
```

- [ ] **Step 3: Write the failing test**

`tests/VibeyReports.CrystalWorker.Tests/CrystalSessionTests.cs`:

```csharp
using System;
using System.IO;
using FluentAssertions;
using VibeyReports.CrystalWorker;
using Xunit;

namespace VibeyReports.CrystalWorker.Tests;

public static class Fixtures
{
    public static string Dir
    {
        get
        {
            var d = AppDomain.CurrentDomain.BaseDirectory;
            for (var i = 0; i < 8 && d != null; i++)
            {
                var candidate = Path.Combine(d, "fixtures");
                if (Directory.Exists(candidate)) return candidate;
                d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar));
            }
            throw new DirectoryNotFoundException("Could not locate tests/fixtures.");
        }
    }

    public static string SampleReport => Path.Combine(Dir, "SampleReport.rpt");
}

public class CrystalSessionTests
{
    [Fact]
    public void Open_BindsToCrystalXiR2AndReportsTheDocumentIsOpen()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        session.Document.Should().NotBeNull();
        session.Document.IsOpen.Should().BeTrue();
        session.Document.ReportDefController.Should().NotBeNull();
    }

    [Fact]
    public void Open_ExposesAtLeastOneSection()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var sections = session.Document.ReportDefController.ReportDefinition.Sections;

        sections.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SaveAs_WritesANewRptFileWithoutTouchingTheOriginal()
    {
        var originalBytes = File.ReadAllBytes(Fixtures.SampleReport);
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");

        try
        {
            using (var session = CrystalSession.Open(Fixtures.SampleReport))
            {
                session.SaveAs(dest, overwrite: false);
            }

            File.Exists(dest).Should().BeTrue();
            new FileInfo(dest).Length.Should().BeGreaterThan(0);
            File.ReadAllBytes(Fixtures.SampleReport).Should().Equal(originalBytes);
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }

    [Fact]
    public void SaveAs_RefusesToOverwriteAnExistingFileUnlessAsked()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        File.WriteAllText(dest, "occupied");

        try
        {
            using var session = CrystalSession.Open(Fixtures.SampleReport);

            Action act = () => session.SaveAs(dest, overwrite: false);

            act.Should().Throw<IOException>().WithMessage("*already exists*");
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }
}
```

- [ ] **Step 4: Create the test project**

The test project must also be `net48` **x86** so it loads the same 32-bit COM interop.

```bash
cd /d/VibeyReports && mkdir -p tests/VibeyReports.CrystalWorker.Tests
```

Write `tests/VibeyReports.CrystalWorker.Tests/VibeyReports.CrystalWorker.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <PlatformTarget>x86</PlatformTarget>
    <Nullable>disable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
    <PackageReference Include="FluentAssertions" Version="8.9.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\VibeyReports.CrystalWorker\VibeyReports.CrystalWorker.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Run the test to verify it fails**

```bash
cd /d/VibeyReports && dotnet sln add src/VibeyReports.CrystalWorker tests/VibeyReports.CrystalWorker.Tests && dotnet test tests/VibeyReports.CrystalWorker.Tests
```

Expected: FAIL — `The type or namespace name 'CrystalSession' could not be found`.

- [ ] **Step 6: Write `CrystalSession.cs`**

```csharp
using System;
using System.IO;
using CrystalDecisions.ReportAppServer.ClientDoc;

namespace VibeyReports.CrystalWorker
{
    /// <summary>
    /// Owns one open ReportClientDocument. All Crystal XI R2 interaction goes through here.
    /// </summary>
    public sealed class CrystalSession : IDisposable
    {
        private ReportClientDocument _doc;
        private bool _disposed;

        private CrystalSession(ReportClientDocument doc, string sourcePath)
        {
            _doc = doc;
            SourcePath = sourcePath;
        }

        public string SourcePath { get; }

        public ISCDReportClientDocument Document
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(CrystalSession));
                return _doc;
            }
        }

        public static CrystalSession Open(string rptPath)
        {
            if (string.IsNullOrWhiteSpace(rptPath)) throw new ArgumentException("Report path is required.", nameof(rptPath));

            var full = Path.GetFullPath(rptPath);
            if (!File.Exists(full)) throw new FileNotFoundException($"Report not found: {full}", full);

            var doc = new ReportClientDocument();
            // Keep the document open until we dispose it explicitly.
            doc.set_AutoClose(false);
            // 0 = default open options.
            doc.Open(full, 0);

            return new CrystalSession(doc, full);
        }

        public void SaveAs(string destinationPath, bool overwrite)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CrystalSession));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", nameof(destinationPath));

            var full = Path.GetFullPath(destinationPath);

            if (string.Equals(full, SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Refusing to overwrite the source report: {full}");

            if (File.Exists(full) && !overwrite)
                throw new IOException($"Destination already exists: {full}. Pass overwrite=true to replace it.");

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(full)) File.Delete(full);

            // SaveAs(name, directory, options). 0 = crReportOptionDefault.
            _doc.SaveAs(Path.GetFileName(full), dir, 0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_doc != null && _doc.IsOpen) _doc.Close();
            }
            catch
            {
                // A failed Close must not mask the real error from the caller.
            }

            _doc = null;
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests
```

Expected: PASS, 4 tests.

**If you see `REGDB_E_CLASSNOTREG` (0x80040154):** the build is not x86. Confirm `<PlatformTarget>x86</PlatformTarget>` is set on **both** the worker and the test project, then `dotnet clean` and rebuild.

**If you see `Could not load file or assembly 'CrystalDecisions.ReportAppServer.ClientDoc'`:** confirm the DLLs were copied to the test output directory, and that `$(CrystalDir)` resolves — run `ls "C:/Program Files (x86)/Business Objects/Common/3.5/managed/dotnet2"` to check.

**If `SaveAs` throws about the third argument:** the 11.5 signature is `SaveAs(string, string, int)`. Verify with `dotnet build` errors, which will print the expected overload.

- [ ] **Step 8: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: bind CrystalWorker to Crystal XI R2 RAS SDK with session open/save"
```

---

## Task 5: Read a `.rpt` into `ReportSchema`

**Files:**
- Create: `src/VibeyReports.CrystalWorker/ReportReader.cs`
- Test: `tests/VibeyReports.CrystalWorker.Tests/ReportReaderTests.cs`

**Interfaces:**
- Consumes: `CrystalSession` (Task 4), `ReportSchema`/`PageInfo`/`SectionInfo`/`ObjectInfo` (Task 1).
- Produces: `ReportReader.Read(CrystalSession session) -> ReportSchema`. Tasks 6, 8 and 10 call this exact signature.

Verified API facts used here (reflected from the installed 11.5 assemblies):
- `ISCRReportObject` exposes `Name`, `Kind` (`CrReportObjectKindEnum`), `Left`, `Top`, `Width`, `Height`, `SectionName`, `Format` (`ObjectFormat`), `Border`.
- Font lives at `FontColor.Font` (a `System.Drawing.Font`) on `ISCRFieldObject` and `ISCRTextObject`.
- Alignment lives at `ISCRReportObject.Format.HorizontalAlignment` (`CrAlignmentEnum`) for every object kind.
- `CrReportObjectKindEnum` values: `crReportObjectKindField`, `crReportObjectKindText`, `crReportObjectKindLine`, `crReportObjectKindBox`, `crReportObjectKindSubreport`, `crReportObjectKindPicture`, `crReportObjectKindChart`, `crReportObjectKindCrosstab`, `crReportObjectKindBlobField`, `crReportObjectKindMap`, `crReportObjectKindOlapGrid`, `crReportObjectKindFieldHeading`.

- [ ] **Step 1: Write the failing test**

`tests/VibeyReports.CrystalWorker.Tests/ReportReaderTests.cs`:

```csharp
using System.Linq;
using FluentAssertions;
using VibeyReports.Contracts;
using VibeyReports.CrystalWorker;
using Xunit;

namespace VibeyReports.CrystalWorker.Tests;

public class ReportReaderTests
{
    [Fact]
    public void Read_ReturnsPageDimensionsInTwips()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);

        // Any real page is at least 3 inches on a side.
        schema.Page.WidthTwips.Should().BeGreaterThan(4320);
        schema.Page.HeightTwips.Should().BeGreaterThan(4320);
        schema.Page.Orientation.Should().BeOneOf("Portrait", "Landscape");
        schema.ReportPath.Should().EndWith("SampleReport.rpt");
    }

    [Fact]
    public void Read_ReturnsSectionsWithStableNamesAndHeights()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);

        schema.Sections.Should().NotBeEmpty();
        schema.Sections.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Name));
        schema.Sections.Should().OnlyContain(s => s.HeightTwips >= 0);
        schema.Sections.Select(s => s.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Read_ClassifiesSectionsIntoRecognisableBands()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);

        var known = new[] { "ReportHeader", "PageHeader", "GroupHeader", "Details", "GroupFooter", "ReportFooter", "PageFooter", "Other" };
        schema.Sections.Should().OnlyContain(s => known.Contains(s.Kind));
    }

    [Fact]
    public void Read_ReturnsObjectsWithUniqueNamesAndNonNegativeGeometry()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);
        var objects = schema.Sections.SelectMany(s => s.Objects).ToList();

        objects.Should().NotBeEmpty();
        objects.Select(o => o.Name).Should().OnlyHaveUniqueItems();
        objects.Should().OnlyContain(o => o.LeftTwips >= 0 && o.TopTwips >= 0);
        objects.Should().OnlyContain(o => o.WidthTwips >= 0 && o.HeightTwips >= 0);
    }

    [Fact]
    public void Read_CapturesFontDetailsForTextAndFieldObjects()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);
        var textish = schema.Sections
            .SelectMany(s => s.Objects)
            .Where(o => o.Kind == "Text" || o.Kind == "Field")
            .ToList();

        textish.Should().NotBeEmpty();
        textish.Should().OnlyContain(o => !string.IsNullOrWhiteSpace(o.FontName));
        textish.Should().OnlyContain(o => o.FontSizePt > 0);
        textish.Should().OnlyContain(o => o.Bold != null);
    }

    [Fact]
    public void Read_CapturesDataSourceForFieldsAndTextForTextObjects()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);
        var objects = schema.Sections.SelectMany(s => s.Objects).ToList();

        objects.Where(o => o.Kind == "Field").Should().OnlyContain(o => o.DataSource != null);
    }

    [Theory]
    [InlineData("Documents.rpt")]
    [InlineData("JournalEntry.rpt")]
    [InlineData("PMSV10_IndPerfOverview.rpt")]
    public void Read_HandlesEveryFixtureWithoutThrowing(string fixtureName)
    {
        var path = System.IO.Path.Combine(Fixtures.Dir, fixtureName);
        using var session = CrystalSession.Open(path);

        var schema = ReportReader.Read(session);

        schema.Sections.Should().NotBeEmpty();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests --filter ReportReaderTests
```

Expected: FAIL — `The name 'ReportReader' does not exist`.

- [ ] **Step 3: Write `ReportReader.cs`**

```csharp
using System;
using System.Drawing;
using CrystalDecisions.ReportAppServer.Controllers;
using CrystalDecisions.ReportAppServer.ReportDefModel;
using VibeyReports.Contracts;

namespace VibeyReports.CrystalWorker
{
    public static class ReportReader
    {
        public static ReportSchema Read(CrystalSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var doc = session.Document;
            var schema = new ReportSchema { ReportPath = session.SourcePath };

            var printOptions = doc.PrintOutputController.GetPrintOptions();
            schema.Page = new PageInfo
            {
                WidthTwips = printOptions.PageContentWidth,
                HeightTwips = printOptions.PageContentHeight,
                MarginLeftTwips = printOptions.PageMargins.leftMargin,
                MarginRightTwips = printOptions.PageMargins.rightMargin,
                MarginTopTwips = printOptions.PageMargins.topMargin,
                MarginBottomTwips = printOptions.PageMargins.bottomMargin,
                Orientation = printOptions.PaperOrientation == CrPaperOrientationEnum.crPaperOrientationLandscape
                    ? "Landscape"
                    : "Portrait"
            };

            var sections = doc.ReportDefController.ReportDefinition.Sections;
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                var info = new SectionInfo
                {
                    Name = section.Name,
                    Kind = ClassifySection(section.Name),
                    HeightTwips = section.Height,
                    Suppressed = section.Format != null && section.Format.EnableSuppress
                };

                var objects = section.ReportObjects;
                for (var j = 0; j < objects.Count; j++)
                {
                    info.Objects.Add(ReadObject((ISCRReportObject)objects[j]));
                }

                schema.Sections.Add(info);
            }

            return schema;
        }

        private static ObjectInfo ReadObject(ISCRReportObject ro)
        {
            var info = new ObjectInfo
            {
                Name = ro.Name,
                Kind = ClassifyKind(ro.Kind),
                LeftTwips = ro.Left,
                TopTwips = ro.Top,
                WidthTwips = ro.Width,
                HeightTwips = ro.Height,
                Alignment = ro.Format == null ? null : ClassifyAlignment(ro.Format.HorizontalAlignment)
            };

            switch (ro)
            {
                case ISCRFieldObject field:
                    ApplyFont(info, field.FontColor?.Font);
                    info.DataSource = field.DataSource ?? "";
                    break;

                case ISCRTextObject text:
                    ApplyFont(info, text.FontColor?.Font);
                    info.Text = text.Text ?? "";
                    break;
            }

            return info;
        }

        private static void ApplyFont(ObjectInfo info, Font font)
        {
            if (font == null) return;
            info.FontName = font.Name;
            info.FontSizePt = font.Size;
            info.Bold = font.Bold;
            info.Italic = font.Italic;
            info.Underline = font.Underline;
        }

        private static string ClassifyKind(CrReportObjectKindEnum kind)
        {
            switch (kind)
            {
                case CrReportObjectKindEnum.crReportObjectKindField: return "Field";
                case CrReportObjectKindEnum.crReportObjectKindText: return "Text";
                case CrReportObjectKindEnum.crReportObjectKindLine: return "Line";
                case CrReportObjectKindEnum.crReportObjectKindBox: return "Box";
                case CrReportObjectKindEnum.crReportObjectKindSubreport: return "Subreport";
                case CrReportObjectKindEnum.crReportObjectKindPicture: return "Picture";
                case CrReportObjectKindEnum.crReportObjectKindChart: return "Chart";
                case CrReportObjectKindEnum.crReportObjectKindCrosstab: return "Crosstab";
                case CrReportObjectKindEnum.crReportObjectKindFieldHeading: return "FieldHeading";
                case CrReportObjectKindEnum.crReportObjectKindBlobField: return "BlobField";
                case CrReportObjectKindEnum.crReportObjectKindMap: return "Map";
                case CrReportObjectKindEnum.crReportObjectKindOlapGrid: return "OlapGrid";
                default: return "Other";
            }
        }

        private static string ClassifyAlignment(CrAlignmentEnum alignment)
        {
            switch (alignment)
            {
                case CrAlignmentEnum.crAlignmentLeft: return "Left";
                case CrAlignmentEnum.crAlignmentRight: return "Right";
                case CrAlignmentEnum.crAlignmentHorizontalCenter: return "Centre";
                case CrAlignmentEnum.crAlignmentJustified: return "Justified";
                default: return "Default";
            }
        }

        /// <summary>
        /// RAS section names encode the band, e.g. "Section1" in area "ReportHeaderArea".
        /// We classify by the report definition's area name prefix, which the section name mirrors.
        /// </summary>
        private static string ClassifySection(string sectionName)
        {
            var n = (sectionName ?? "").ToUpperInvariant();
            if (n.Contains("REPORTHEADER")) return "ReportHeader";
            if (n.Contains("PAGEHEADER")) return "PageHeader";
            if (n.Contains("GROUPHEADER")) return "GroupHeader";
            if (n.Contains("DETAIL")) return "Details";
            if (n.Contains("GROUPFOOTER")) return "GroupFooter";
            if (n.Contains("REPORTFOOTER")) return "ReportFooter";
            if (n.Contains("PAGEFOOTER")) return "PageFooter";
            return "Other";
        }
    }
}
```

- [ ] **Step 4: Run the tests**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests --filter ReportReaderTests
```

Expected: PASS, 9 tests.

**If `Read_ClassifiesSectionsIntoRecognisableBands` fails** because every section comes back `"Other"`: RAS names sections `Section1`, `Section2`… rather than encoding the band. Replace `ClassifySection(section.Name)` with a lookup over the areas — iterate `doc.ReportDefController.ReportDefinition.Areas`, and for each area use `area.Kind` (`CrAreaSectionKindEnum`: `crAreaSectionKindReportHeader`, `crAreaSectionKindPageHeader`, `crAreaSectionKindGroupHeader`, `crAreaSectionKindDetail`, `crAreaSectionKindGroupFooter`, `crAreaSectionKindReportFooter`, `crAreaSectionKindPageFooter`) to label each of `area.Sections`. Build a `Dictionary<string,string>` from section name to band before the section loop and read from it. Keep the test unchanged — it is asserting the right thing.

- [ ] **Step 5: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: extract report layout into ReportSchema via RAS SDK"
```

---

## Task 6: Apply a validated `LayoutPlan` to a report

**Files:**
- Create: `src/VibeyReports.CrystalWorker/LayoutApplier.cs`
- Test: `tests/VibeyReports.CrystalWorker.Tests/LayoutApplierTests.cs`

**Interfaces:**
- Consumes: `CrystalSession`, `ReportReader.Read`, `LayoutPlan`, `LayoutOperation`, `LayoutActions`, `LayoutPlanValidator.Validate`.
- Produces: `LayoutApplier.Apply(CrystalSession session, LayoutPlan plan) -> int` (returns the number of operations applied), and `LayoutApplier.InvalidPlanException` (carries `ValidationResult Result`). Tasks 8 and 10 call these.

The critical RAS idiom, confirmed against the installed assemblies: **you cannot mutate a live report object.** You clone it, change the clone, then hand both to `ReportObjectController.Modify(old, new)`.

- [ ] **Step 1: Write the failing test**

`tests/VibeyReports.CrystalWorker.Tests/LayoutApplierTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using VibeyReports.Contracts;
using VibeyReports.CrystalWorker;
using Xunit;

namespace VibeyReports.CrystalWorker.Tests;

public class LayoutApplierTests
{
    private static string TempRpt() => Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");

    /// <summary>Applies a plan, saves, reopens, and hands back the resulting schema.</summary>
    private static ReportSchema ApplyAndReread(LayoutPlan plan, out string savedPath)
    {
        var dest = TempRpt();
        using (var session = CrystalSession.Open(Fixtures.SampleReport))
        {
            LayoutApplier.Apply(session, plan);
            session.SaveAs(dest, overwrite: false);
        }

        savedPath = dest;
        using var reopened = CrystalSession.Open(dest);
        return ReportReader.Read(reopened);
    }

    private static string FirstTextOrFieldName()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);
        var schema = ReportReader.Read(session);
        return schema.Sections
            .SelectMany(s => s.Objects)
            .First(o => o.Kind == "Text" || o.Kind == "Field")
            .Name;
    }

    [Fact]
    public void Apply_MovesAnObjectAndThePositionSurvivesSaveAndReopen()
    {
        var name = FirstTextOrFieldName();
        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.Move, Target = name, LeftTwips = 720, TopTwips = 0 } }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var moved = schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == name);
            moved.LeftTwips.Should().Be(720);
            moved.TopTwips.Should().Be(0);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_ResizesAnObjectAndTheSizeSurvivesSaveAndReopen()
    {
        var name = FirstTextOrFieldName();
        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.Resize, Target = name, WidthTwips = 1440, HeightTwips = 240 } }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var resized = schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == name);
            resized.WidthTwips.Should().Be(1440);
            resized.HeightTwips.Should().Be(240);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_SetsFontNameSizeAndBoldTogether()
    {
        var name = FirstTextOrFieldName();
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.SetFont, Target = name, FontName = "Calibri" },
                new LayoutOperation { Action = LayoutActions.SetFontSize, Target = name, FontSizePt = 14f },
                new LayoutOperation { Action = LayoutActions.SetBold, Target = name, Bold = true }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var styled = schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == name);
            styled.FontName.Should().Be("Calibri");
            styled.FontSizePt.Should().Be(14f);
            styled.Bold.Should().BeTrue();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_SetsHorizontalAlignment()
    {
        var name = FirstTextOrFieldName();
        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.SetAlignment, Target = name, Alignment = "Centre" } }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == name)
                  .Alignment.Should().Be("Centre");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_AddsATextObjectThatIsPresentAfterReopen()
    {
        string sectionName;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
            sectionName = ReportReader.Read(s).Sections.First(x => x.HeightTwips >= 400).Name;

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddText, Section = sectionName, NewName = "VibeyTitle",
                    Text = "Payroll Summary", LeftTwips = 0, TopTwips = 0, WidthTwips = 2880, HeightTwips = 320
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var added = schema.Sections.SelectMany(s => s.Objects).SingleOrDefault(o => o.Name == "VibeyTitle");
            added.Should().NotBeNull();
            added!.Kind.Should().Be("Text");
            added.Text.Should().Contain("Payroll Summary");
            added.WidthTwips.Should().Be(2880);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_AddsALineAndABox()
    {
        string sectionName;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
            sectionName = ReportReader.Read(s).Sections.First(x => x.HeightTwips >= 400).Name;

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.AddLine, Section = sectionName, NewName = "VibeyRule",
                    LeftTwips = 0, TopTwips = 350, WidthTwips = 2880, HeightTwips = 0 },
                new LayoutOperation { Action = LayoutActions.AddBox, Section = sectionName, NewName = "VibeyFrame",
                    LeftTwips = 0, TopTwips = 0, WidthTwips = 2880, HeightTwips = 340 }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var names = schema.Sections.SelectMany(s => s.Objects).Select(o => o.Name).ToList();
            names.Should().Contain("VibeyRule");
            names.Should().Contain("VibeyFrame");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_ResizesASection()
    {
        string sectionName;
        int original;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
        {
            var sec = ReportReader.Read(s).Sections.First(x => x.Objects.Count == 0 || x.HeightTwips > 0);
            sectionName = sec.Name;
            original = sec.HeightTwips;
        }

        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.ResizeSection, Section = sectionName, HeightTwips = original + 360 } }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            schema.Sections.Single(s => s.Name == sectionName).HeightTwips.Should().Be(original + 360);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_ThrowsInvalidPlanExceptionAndChangesNothingWhenThePlanIsInvalid()
    {
        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.Move, Target = "DefinitelyNotAnObject", LeftTwips = 1, TopTwips = 1 } }
        };

        using var session = CrystalSession.Open(Fixtures.SampleReport);
        var before = ReportReader.Read(session);

        Action act = () => LayoutApplier.Apply(session, plan);

        act.Should().Throw<LayoutApplier.InvalidPlanException>()
           .Which.Result.Errors.Should().NotBeEmpty();

        var after = ReportReader.Read(session);
        after.Sections.SelectMany(s => s.Objects).Count()
             .Should().Be(before.Sections.SelectMany(s => s.Objects).Count());
    }

    [Fact]
    public void Apply_ReturnsTheNumberOfOperationsApplied()
    {
        var name = FirstTextOrFieldName();
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.SetBold, Target = name, Bold = true },
                new LayoutOperation { Action = LayoutActions.SetFontSize, Target = name, FontSizePt = 12f }
            }
        };

        using var session = CrystalSession.Open(Fixtures.SampleReport);

        LayoutApplier.Apply(session, plan).Should().Be(2);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests --filter LayoutApplierTests
```

Expected: FAIL — `The name 'LayoutApplier' does not exist`.

- [ ] **Step 3: Write `LayoutApplier.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using CrystalDecisions.ReportAppServer.Controllers;
using CrystalDecisions.ReportAppServer.ReportDefModel;
using VibeyReports.Contracts;

namespace VibeyReports.CrystalWorker
{
    public static class LayoutApplier
    {
        public sealed class InvalidPlanException : Exception
        {
            public InvalidPlanException(ValidationResult result)
                : base("Layout plan failed validation: " +
                       string.Join(" | ", result.Errors.Select(e => $"[op {e.OperationIndex}] {e.Message}")))
            {
                Result = result;
            }

            public ValidationResult Result { get; }
        }

        /// <summary>
        /// Validates the plan against the report's current layout, then applies every operation.
        /// Throws <see cref="InvalidPlanException"/> before touching the report if validation fails.
        /// </summary>
        public static int Apply(CrystalSession session, LayoutPlan plan)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var schema = ReportReader.Read(session);
            var validation = LayoutPlanValidator.Validate(plan, schema);
            if (!validation.IsValid) throw new InvalidPlanException(validation);

            var doc = session.Document;
            var applied = 0;

            foreach (var op in plan.Operations)
            {
                switch (op.Action)
                {
                    case LayoutActions.Move:
                        ModifyObject(doc, op.Target, o => { o.Left = op.LeftTwips.Value; o.Top = op.TopTwips.Value; });
                        break;

                    case LayoutActions.Resize:
                        ModifyObject(doc, op.Target, o => { o.Width = op.WidthTwips.Value; o.Height = op.HeightTwips.Value; });
                        break;

                    case LayoutActions.SetFont:
                        ModifyObject(doc, op.Target, o => WithFont(o, f => new Font(op.FontName, f.Size, f.Style)));
                        break;

                    case LayoutActions.SetFontSize:
                        ModifyObject(doc, op.Target, o => WithFont(o, f => new Font(f.Name, op.FontSizePt.Value, f.Style)));
                        break;

                    case LayoutActions.SetBold:
                        ModifyObject(doc, op.Target, o => WithFont(o, f =>
                        {
                            var style = op.Bold.Value ? f.Style | FontStyle.Bold : f.Style & ~FontStyle.Bold;
                            return new Font(f.Name, f.Size, style);
                        }));
                        break;

                    case LayoutActions.SetAlignment:
                        ModifyObject(doc, op.Target, o => o.Format.HorizontalAlignment = ParseAlignment(op.Alignment));
                        break;

                    case LayoutActions.AddText:
                        AddText(doc, op);
                        break;

                    case LayoutActions.AddLine:
                        AddLine(doc, op);
                        break;

                    case LayoutActions.AddBox:
                        AddBox(doc, op);
                        break;

                    case LayoutActions.ResizeSection:
                        ResizeSection(doc, op.Section, op.HeightTwips.Value);
                        break;

                    default:
                        throw new InvalidOperationException($"Unhandled action \"{op.Action}\" reached the applier; the validator should have rejected it.");
                }

                applied++;
            }

            return applied;
        }

        // --- object mutation -------------------------------------------------

        /// <summary>
        /// RAS objects cannot be mutated in place. Clone, mutate the clone, then Modify(old, new).
        /// </summary>
        private static void ModifyObject(
            CrystalDecisions.ReportAppServer.ClientDoc.ISCDReportClientDocument doc,
            string objectName,
            Action<ISCRReportObject> mutate)
        {
            var existing = FindObject(doc, objectName);
            var clone = (ISCRReportObject)existing.Clone(true);
            mutate(clone);
            doc.ReportDefController.ReportObjectController.Modify(existing, clone);
        }

        private static ISCRReportObject FindObject(
            CrystalDecisions.ReportAppServer.ClientDoc.ISCDReportClientDocument doc,
            string objectName)
        {
            var all = doc.ReportDefController.ReportObjectController.GetAllReportObjects();
            for (var i = 0; i < all.Count; i++)
            {
                var ro = (ISCRReportObject)all[i];
                if (string.Equals(ro.Name, objectName, StringComparison.OrdinalIgnoreCase)) return ro;
            }
            throw new InvalidOperationException($"Object \"{objectName}\" was not found; the validator should have rejected it.");
        }

        /// <summary>Applies a font transform to whichever object kinds carry a FontColor.</summary>
        private static void WithFont(ISCRReportObject obj, Func<Font, Font> transform)
        {
            switch (obj)
            {
                case ISCRFieldObject field:
                    field.FontColor.Font = transform(field.FontColor.Font);
                    break;
                case ISCRTextObject text:
                    text.FontColor.Font = transform(text.FontColor.Font);
                    break;
                default:
                    throw new InvalidOperationException($"Object \"{obj.Name}\" ({obj.Kind}) has no font to change.");
            }
        }

        private static CrAlignmentEnum ParseAlignment(string alignment)
        {
            switch ((alignment ?? "").ToUpperInvariant())
            {
                case "LEFT": return CrAlignmentEnum.crAlignmentLeft;
                case "RIGHT": return CrAlignmentEnum.crAlignmentRight;
                case "CENTRE":
                case "CENTER": return CrAlignmentEnum.crAlignmentHorizontalCenter;
                case "JUSTIFIED": return CrAlignmentEnum.crAlignmentJustified;
                default: throw new InvalidOperationException($"Unsupported alignment \"{alignment}\".");
            }
        }

        // --- object creation -------------------------------------------------

        private static Section FindSection(
            CrystalDecisions.ReportAppServer.ClientDoc.ISCDReportClientDocument doc,
            string sectionName)
        {
            var sections = doc.ReportDefController.ReportDefinition.Sections;
            for (var i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                if (string.Equals(s.Name, sectionName, StringComparison.OrdinalIgnoreCase)) return s;
            }
            throw new InvalidOperationException($"Section \"{sectionName}\" was not found; the validator should have rejected it.");
        }

        private static void AddText(
            CrystalDecisions.ReportAppServer.ClientDoc.ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            var text = new TextObjectClass
            {
                Name = op.NewName,
                Left = op.LeftTwips.Value,
                Top = op.TopTwips.Value,
                Width = op.WidthTwips.Value,
                Height = op.HeightTwips.Value
            };

            var element = new ParagraphTextElementClass { Text = op.Text };
            var paragraph = new ParagraphClass();
            paragraph.ParagraphElements.Add(element);
            text.Paragraphs.Add(paragraph);

            doc.ReportDefController.ReportObjectController.Add(text, section, -1);
        }

        private static void AddLine(
            CrystalDecisions.ReportAppServer.ClientDoc.ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            var line = new LineObjectClass
            {
                Name = op.NewName,
                Left = op.LeftTwips.Value,
                Top = op.TopTwips.Value,
                Width = op.WidthTwips.Value,
                Height = op.HeightTwips.Value,
                Right = op.LeftTwips.Value + op.WidthTwips.Value,
                Bottom = op.TopTwips.Value + op.HeightTwips.Value,
                LineThickness = 15,
                LineStyle = CrLineStyleEnum.crLineStyleSingle,
                EndSectionName = section.Name
            };

            doc.ReportDefController.ReportObjectController.Add(line, section, -1);
        }

        private static void AddBox(
            CrystalDecisions.ReportAppServer.ClientDoc.ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            var box = new BoxObjectClass
            {
                Name = op.NewName,
                Left = op.LeftTwips.Value,
                Top = op.TopTwips.Value,
                Width = op.WidthTwips.Value,
                Height = op.HeightTwips.Value,
                Right = op.LeftTwips.Value + op.WidthTwips.Value,
                Bottom = op.TopTwips.Value + op.HeightTwips.Value,
                LineThickness = 15,
                LineStyle = CrLineStyleEnum.crLineStyleSingle,
                EndSectionName = section.Name
            };

            doc.ReportDefController.ReportObjectController.Add(box, section, -1);
        }

        private static void ResizeSection(
            CrystalDecisions.ReportAppServer.ClientDoc.ISCDReportClientDocument doc,
            string sectionName,
            int heightTwips)
        {
            var section = FindSection(doc, sectionName);
            doc.ReportDefController.ReportSectionController.SetProperty(
                section,
                CrReportSectionPropertyEnum.crReportSectionPropertyHeight,
                heightTwips);
        }
    }
}
```

- [ ] **Step 4: Run the tests**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests --filter LayoutApplierTests
```

Expected: PASS, 9 tests.

**If `Apply_AddsATextObjectThatIsPresentAfterReopen` throws on `paragraph.ParagraphElements.Add(element)`:** the collection may require the element be cast to `ISCRParagraphElement` first, or `Paragraphs`/`ParagraphElements` may need explicit construction (`text.Paragraphs = new ParagraphsClass()`). Build errors will name the expected type. Do not weaken the test.

**If font changes do not survive reopen:** confirm you are setting the font on the *clone* before `Modify`, not on the object returned by `GetAllReportObjects()`.

- [ ] **Step 5: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: apply validated layout plans to reports via RAS clone-modify"
```

---

## Task 7: Render a report to PDF

**Files:**
- Create: `src/VibeyReports.CrystalWorker/ReportRenderer.cs`
- Test: `tests/VibeyReports.CrystalWorker.Tests/ReportRendererTests.cs`

**Interfaces:**
- Consumes: `CrystalSession`.
- Produces: `ReportRenderer.ExportPdf(CrystalSession session) -> byte[]`. Task 10 calls this via the worker protocol.

Verified: `PrintOutputController.Export(CrReportExportFormatEnum, int)` returns a `ByteArray`; the PDF value is `CrReportExportFormatEnum.crReportExportFormatPDF`.

- [ ] **Step 1: Write the failing test**

`tests/VibeyReports.CrystalWorker.Tests/ReportRendererTests.cs`:

```csharp
using System;
using System.Text;
using FluentAssertions;
using VibeyReports.CrystalWorker;
using Xunit;

namespace VibeyReports.CrystalWorker.Tests;

public class ReportRendererTests
{
    [Fact]
    public void ExportPdf_ReturnsBytesThatStartWithThePdfMagicNumber()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var pdf = ReportRenderer.ExportPdf(session);

        pdf.Should().NotBeNull();
        pdf.Length.Should().BeGreaterThan(1000);
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public void ExportPdf_ProducesAFileEndingWithEof()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var pdf = ReportRenderer.ExportPdf(session);
        var tail = Encoding.ASCII.GetString(pdf, Math.Max(0, pdf.Length - 32), Math.Min(32, pdf.Length));

        tail.Should().Contain("%%EOF");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests --filter ReportRendererTests
```

Expected: FAIL — `The name 'ReportRenderer' does not exist`.

- [ ] **Step 3: Write `ReportRenderer.cs`**

```csharp
using System;
using System.IO;
using CrystalDecisions.ReportAppServer.ReportDefModel;

namespace VibeyReports.CrystalWorker
{
    public static class ReportRenderer
    {
        /// <summary>
        /// Exports the report to PDF. The report is rendered with whatever saved data or
        /// database connection it already carries; Vibey Reports never changes either.
        /// </summary>
        public static byte[] ExportPdf(CrystalSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var byteArray = session.Document.PrintOutputController.Export(
                CrReportExportFormatEnum.crReportExportFormatPDF, 0);

            using (var stream = byteArray.get_Stream() as Stream)
            {
                if (stream == null) throw new InvalidOperationException("Crystal returned an empty export stream.");

                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests --filter ReportRendererTests
```

Expected: PASS, 2 tests.

**If `get_Stream()` does not exist on `ByteArray`:** the 11.5 `ByteArray` exposes the bytes via `ItemArray` or an indexer instead. Inspect with the same reflection recipe used during planning:

```bash
powershell -NoProfile -Command "$p='C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2'; $h=[System.ResolveEventHandler]{param($s,$e) $n=($e.Name -split ',')[0]; $f=Join-Path $p \"$n.dll\"; if(Test-Path $f){[System.Reflection.Assembly]::ReflectionOnlyLoadFrom($f)}else{$null}}; [System.AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($h); $a=[System.Reflection.Assembly]::ReflectionOnlyLoadFrom(\"$p\CrystalDecisions.ReportAppServer.CommLayer.dll\"); $a.GetTypes() | Where-Object {$_.Name -eq 'ByteArray'} | ForEach-Object { $_.GetMembers() | ForEach-Object { $_.Name } }"
```

**If the export throws a database logon error:** the fixture report has no saved data and wants a live connection. Switch the renderer test to a fixture that carries saved data, or mark that specific test `Skip` with a comment naming the reason. Do not add database-connection code to the worker — Global Constraint 5 forbids it.

- [ ] **Step 5: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: export reports to PDF via RAS PrintOutputController"
```

---

## Task 8: Worker process protocol (stdin JSON in, stdout JSON out)

**Files:**
- Create: `src/VibeyReports.Contracts/WorkerProtocol.cs`
- Create: `src/VibeyReports.CrystalWorker/Program.cs`
- Test: `tests/VibeyReports.Contracts.Tests/WorkerProtocolTests.cs`
- Test: `tests/VibeyReports.CrystalWorker.Tests/ProgramEndToEndTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–7.
- Produces: `WorkerRequest` (`Command`, `ReportPath`, `OutputPath`, `Overwrite`, `Plan`), `WorkerResponse` (`Ok`, `Error`, `Schema`, `OperationsApplied`, `OutputPath`, `PdfBase64`, `ValidationErrors`), and the worker's three commands: `"read"`, `"apply"`, `"render"`. Task 9's `CrystalWorkerClient` binds to these exact names.

The contract: **stdout carries exactly one JSON object and nothing else.** All diagnostics go to stderr. Exit code 0 means the JSON was produced (even for `ok: false`); non-zero means the worker itself crashed.

- [ ] **Step 1: Write the failing contract test**

`tests/VibeyReports.Contracts.Tests/WorkerProtocolTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using VibeyReports.Contracts;
using Xunit;

namespace VibeyReports.Contracts.Tests;

public class WorkerProtocolTests
{
    [Fact]
    public void WorkerRequest_RoundTripsAnApplyCommandWithAPlan()
    {
        var request = new WorkerRequest
        {
            Command = WorkerCommands.Apply,
            ReportPath = @"C:\in.rpt",
            OutputPath = @"C:\out.rpt",
            Overwrite = true,
            Plan = new LayoutPlan
            {
                Operations = { new LayoutOperation { Action = LayoutActions.Move, Target = "A", LeftTwips = 1, TopTwips = 2 } }
            }
        };

        var json = JsonSerializer.Serialize(request, VibeyJson.Options);
        var back = JsonSerializer.Deserialize<WorkerRequest>(json, VibeyJson.Options)!;

        back.Command.Should().Be("apply");
        back.Overwrite.Should().BeTrue();
        back.Plan!.Operations.Should().ContainSingle();
    }

    [Fact]
    public void WorkerResponse_Failure_CarriesTheErrorAndValidationDetail()
    {
        var response = WorkerResponse.Failure("bad plan", new ValidationResult
        {
            IsValid = false,
            Errors = { new ValidationError { OperationIndex = 0, Message = "nope" } }
        });

        var json = JsonSerializer.Serialize(response, VibeyJson.Options);
        var back = JsonSerializer.Deserialize<WorkerResponse>(json, VibeyJson.Options)!;

        back.Ok.Should().BeFalse();
        back.Error.Should().Be("bad plan");
        back.ValidationErrors.Should().ContainSingle().Which.Message.Should().Be("nope");
    }

    [Fact]
    public void WorkerCommands_ContainsTheThreeSupportedCommands()
    {
        WorkerCommands.All.Should().BeEquivalentTo(new[] { "read", "apply", "render" });
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Contracts.Tests --filter WorkerProtocolTests
```

Expected: FAIL — `The type or namespace name 'WorkerRequest' could not be found`.

- [ ] **Step 3: Write `WorkerProtocol.cs`**

```csharp
using System.Collections.Generic;

namespace VibeyReports.Contracts;

public static class WorkerCommands
{
    public const string Read = "read";
    public const string Apply = "apply";
    public const string Render = "render";

    public static readonly string[] All = { Read, Apply, Render };
}

public sealed class WorkerRequest
{
    public string Command { get; set; } = "";
    public string ReportPath { get; set; } = "";
    /// <summary>Destination for "apply". Ignored by "read" and "render".</summary>
    public string? OutputPath { get; set; }
    public bool Overwrite { get; set; }
    /// <summary>Required for "apply".</summary>
    public LayoutPlan? Plan { get; set; }
}

public sealed class WorkerResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    public ReportSchema? Schema { get; set; }
    public int? OperationsApplied { get; set; }
    public string? OutputPath { get; set; }
    /// <summary>Base64-encoded PDF, set by "render".</summary>
    public string? PdfBase64 { get; set; }
    public List<ValidationError>? ValidationErrors { get; set; }

    public static WorkerResponse Success() => new WorkerResponse { Ok = true };

    public static WorkerResponse Failure(string error, ValidationResult? validation = null) =>
        new WorkerResponse
        {
            Ok = false,
            Error = error,
            ValidationErrors = validation?.Errors
        };
}
```

- [ ] **Step 4: Run the contract test to verify it passes**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Contracts.Tests --filter WorkerProtocolTests
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Write the failing end-to-end worker test**

`tests/VibeyReports.CrystalWorker.Tests/ProgramEndToEndTests.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
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

    [Fact]
    public void UnknownCommand_ReturnsOkFalseRatherThanCrashing()
    {
        var response = Run(new WorkerRequest { Command = "dropDatabase", ReportPath = Fixtures.SampleReport });

        response.Ok.Should().BeFalse();
        response.Error.Should().Contain("dropDatabase");
    }
}
```

Add `using System.Linq;` to the file's usings.

- [ ] **Step 6: Run it to verify it fails**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests --filter ProgramEndToEndTests
```

Expected: FAIL — the worker exe has no `Main` that speaks this protocol yet.

- [ ] **Step 7: Write `Program.cs`**

```csharp
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using VibeyReports.Contracts;

namespace VibeyReports.CrystalWorker
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            WorkerResponse response;
            try
            {
                var raw = Console.In.ReadToEnd();
                var request = JsonSerializer.Deserialize<WorkerRequest>(raw, VibeyJson.Options);
                if (request == null) throw new InvalidOperationException("Empty request.");

                response = Handle(request);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                response = WorkerResponse.Failure(ex.Message);
            }

            Console.Out.Write(JsonSerializer.Serialize(response, VibeyJson.Options));
            Console.Out.Flush();
            return 0;
        }

        private static WorkerResponse Handle(WorkerRequest request)
        {
            switch (request.Command)
            {
                case WorkerCommands.Read:
                    using (var session = CrystalSession.Open(request.ReportPath))
                    {
                        var response = WorkerResponse.Success();
                        response.Schema = ReportReader.Read(session);
                        return response;
                    }

                case WorkerCommands.Apply:
                {
                    if (request.Plan == null) return WorkerResponse.Failure("\"apply\" requires a \"plan\".");
                    if (string.IsNullOrWhiteSpace(request.OutputPath)) return WorkerResponse.Failure("\"apply\" requires an \"outputPath\".");

                    using (var session = CrystalSession.Open(request.ReportPath))
                    {
                        int applied;
                        try
                        {
                            applied = LayoutApplier.Apply(session, request.Plan);
                        }
                        catch (LayoutApplier.InvalidPlanException ex)
                        {
                            return WorkerResponse.Failure("Layout plan failed validation.", ex.Result);
                        }

                        session.SaveAs(request.OutputPath, request.Overwrite);

                        var response = WorkerResponse.Success();
                        response.OperationsApplied = applied;
                        response.OutputPath = Path.GetFullPath(request.OutputPath);
                        response.Schema = ReportReader.Read(session);
                        return response;
                    }
                }

                case WorkerCommands.Render:
                    using (var session = CrystalSession.Open(request.ReportPath))
                    {
                        var response = WorkerResponse.Success();
                        response.PdfBase64 = Convert.ToBase64String(ReportRenderer.ExportPdf(session));
                        return response;
                    }

                default:
                    return WorkerResponse.Failure(
                        $"\"{request.Command}\" is not a supported command. Supported: {string.Join(", ", WorkerCommands.All)}.");
            }
        }
    }
}
```

- [ ] **Step 8: Run the tests**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests
```

Expected: PASS — all worker tests, 20 total.

- [ ] **Step 9: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: add stdin/stdout JSON protocol to CrystalWorker"
```

---

## Task 9: MCP server skeleton and worker client

**Files:**
- Create: `src/VibeyReports.Mcp/VibeyReports.Mcp.csproj`
- Create: `src/VibeyReports.Mcp/WorkerLocator.cs`
- Create: `src/VibeyReports.Mcp/CrystalWorkerClient.cs`
- Create: `src/VibeyReports.Mcp/Program.cs`
- Test: `tests/VibeyReports.Mcp.Tests/VibeyReports.Mcp.Tests.csproj`
- Test: `tests/VibeyReports.Mcp.Tests/CrystalWorkerClientTests.cs`

**Interfaces:**
- Consumes: `WorkerRequest`, `WorkerResponse`, `WorkerCommands`, `LayoutPlan`, `VibeyJson.Options`.
- Produces: `WorkerLocator.Find() -> string`, and `CrystalWorkerClient` with `Task<WorkerResponse> ReadAsync(string reportPath, CancellationToken)`, `Task<WorkerResponse> ApplyAsync(string reportPath, string outputPath, LayoutPlan plan, bool overwrite, CancellationToken)`, `Task<WorkerResponse> RenderAsync(string reportPath, CancellationToken)`. Task 10's tools call these exact methods.

`VIBEY_WORKER_PATH` overrides worker discovery; this is how the tests point at a build output and how the MCP registration will point at the published exe.

- [ ] **Step 1: Create the MCP project**

```bash
cd /d/VibeyReports && dotnet new console -n VibeyReports.Mcp -o src/VibeyReports.Mcp -f net10.0
```

Replace `src/VibeyReports.Mcp/VibeyReports.Mcp.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>VibeyReports.Mcp</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.7" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" Version="10.0.7" />
    <PackageReference Include="ModelContextProtocol" Version="1.2.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\VibeyReports.Contracts\VibeyReports.Contracts.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing test**

`tests/VibeyReports.Mcp.Tests/CrystalWorkerClientTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using VibeyReports.Contracts;
using VibeyReports.Mcp;
using Xunit;

namespace VibeyReports.Mcp.Tests;

public static class Fixtures
{
    public static string Dir
    {
        get
        {
            var d = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && d != null; i++)
            {
                var candidate = Path.Combine(d, "fixtures");
                if (Directory.Exists(candidate)) return candidate;
                d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar));
            }
            throw new DirectoryNotFoundException("Could not locate tests/fixtures.");
        }
    }

    public static string SampleReport => Path.Combine(Dir, "SampleReport.rpt");
}

public class CrystalWorkerClientTests
{
    private static CrystalWorkerClient Client() => new CrystalWorkerClient(WorkerLocator.Find());

    [Fact]
    public void WorkerLocator_FindsTheCrystalWorkerExecutable()
    {
        var path = WorkerLocator.Find();

        path.Should().EndWith("VibeyReports.CrystalWorker.exe");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_ReturnsASchemaForARealReport()
    {
        var response = await Client().ReadAsync(Fixtures.SampleReport, CancellationToken.None);

        response.Ok.Should().BeTrue(because: response.Error);
        response.Schema!.Sections.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_ReturnsOkFalseForAMissingFileRatherThanThrowing()
    {
        var response = await Client().ReadAsync(@"C:\definitely\not\here.rpt", CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ApplyAsync_RoundTripsAPlanAndWritesTheOutput()
    {
        var read = await Client().ReadAsync(Fixtures.SampleReport, CancellationToken.None);
        var target = read.Schema!.Sections.SelectMany(s => s.Objects).First(o => o.Kind is "Text" or "Field");

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        try
        {
            var plan = new LayoutPlan
            {
                Operations = { new LayoutOperation { Action = LayoutActions.SetBold, Target = target.Name, Bold = true } }
            };

            var response = await Client().ApplyAsync(Fixtures.SampleReport, dest, plan, overwrite: false, CancellationToken.None);

            response.Ok.Should().BeTrue(because: response.Error);
            response.OperationsApplied.Should().Be(1);
            File.Exists(dest).Should().BeTrue();
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task RenderAsync_ReturnsPdfBytes()
    {
        var response = await Client().RenderAsync(Fixtures.SampleReport, CancellationToken.None);

        response.Ok.Should().BeTrue(because: response.Error);
        var bytes = Convert.FromBase64String(response.PdfBase64!);
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
    }
}
```

- [ ] **Step 3: Create the test project and run to verify it fails**

```bash
cd /d/VibeyReports && dotnet new xunit -n VibeyReports.Mcp.Tests -o tests/VibeyReports.Mcp.Tests -f net10.0 && rm -f tests/VibeyReports.Mcp.Tests/UnitTest1.cs && dotnet add tests/VibeyReports.Mcp.Tests package FluentAssertions --version 8.9.0 && dotnet add tests/VibeyReports.Mcp.Tests reference src/VibeyReports.Mcp && dotnet sln add src/VibeyReports.Mcp tests/VibeyReports.Mcp.Tests
```

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Mcp.Tests
```

Expected: FAIL — `The type or namespace name 'WorkerLocator' could not be found`.

- [ ] **Step 4: Write `WorkerLocator.cs`**

```csharp
namespace VibeyReports.Mcp;

public static class WorkerLocator
{
    public const string OverrideVariable = "VIBEY_WORKER_PATH";
    private const string ExeName = "VibeyReports.CrystalWorker.exe";

    /// <summary>
    /// Finds the x86 Crystal worker. Honours VIBEY_WORKER_PATH, then looks next to this
    /// assembly, then walks up looking for the worker's build output.
    /// </summary>
    public static string Find()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            if (!File.Exists(overridden))
                throw new FileNotFoundException($"{OverrideVariable} points at a file that does not exist: {overridden}", overridden);
            return Path.GetFullPath(overridden);
        }

        var sideBySide = Path.Combine(AppContext.BaseDirectory, ExeName);
        if (File.Exists(sideBySide)) return sideBySide;

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Directory.GetDirectories(dir, "VibeyReports.CrystalWorker", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetDirectories(dir, "src", SearchOption.TopDirectoryOnly))
                .SelectMany(d => Directory.Exists(d)
                    ? Directory.GetFiles(d, ExeName, SearchOption.AllDirectories)
                    : Array.Empty<string>())
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (candidate is not null) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new FileNotFoundException(
            $"Could not find {ExeName}. Build src/VibeyReports.CrystalWorker, or set {OverrideVariable}.");
    }
}
```

- [ ] **Step 5: Write `CrystalWorkerClient.cs`**

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VibeyReports.Contracts;

namespace VibeyReports.Mcp;

/// <summary>
/// Runs the x86 .NET Framework Crystal worker as a child process, one request per run.
/// One process per call keeps the legacy COM state from leaking between operations.
/// </summary>
public sealed class CrystalWorkerClient
{
    private readonly string _workerPath;
    private readonly TimeSpan _timeout;

    public CrystalWorkerClient(string workerPath, TimeSpan? timeout = null)
    {
        _workerPath = workerPath ?? throw new ArgumentNullException(nameof(workerPath));
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
    }

    public Task<WorkerResponse> ReadAsync(string reportPath, CancellationToken ct) =>
        InvokeAsync(new WorkerRequest { Command = WorkerCommands.Read, ReportPath = reportPath }, ct);

    public Task<WorkerResponse> ApplyAsync(string reportPath, string outputPath, LayoutPlan plan, bool overwrite, CancellationToken ct) =>
        InvokeAsync(new WorkerRequest
        {
            Command = WorkerCommands.Apply,
            ReportPath = reportPath,
            OutputPath = outputPath,
            Overwrite = overwrite,
            Plan = plan
        }, ct);

    public Task<WorkerResponse> RenderAsync(string reportPath, CancellationToken ct) =>
        InvokeAsync(new WorkerRequest { Command = WorkerCommands.Render, ReportPath = reportPath }, ct);

    private async Task<WorkerResponse> InvokeAsync(WorkerRequest request, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_workerPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_workerPath)!
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start the Crystal worker: {_workerPath}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, VibeyJson.Options));
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return WorkerResponse.Failure($"The Crystal worker did not finish within {_timeout.TotalSeconds:F0}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (string.IsNullOrWhiteSpace(stdout))
            return WorkerResponse.Failure($"The Crystal worker produced no output. Exit code {process.ExitCode}. stderr: {Truncate(stderr)}");

        try
        {
            return JsonSerializer.Deserialize<WorkerResponse>(stdout, VibeyJson.Options)
                   ?? WorkerResponse.Failure("The Crystal worker returned an empty JSON document.");
        }
        catch (JsonException ex)
        {
            return WorkerResponse.Failure($"Could not parse the worker's response: {ex.Message}. Output began: {Truncate(stdout)}");
        }
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= 800 ? s : s[..800] + "…";
}
```

- [ ] **Step 6: Write a minimal `Program.cs` so the project builds**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeyReports.Mcp;

var builder = Host.CreateApplicationBuilder(args);

// MCP speaks JSON-RPC over stdout. All logging must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(_ => new CrystalWorkerClient(WorkerLocator.Find()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

- [ ] **Step 7: Build the worker, then run the MCP tests**

The MCP tests need the worker exe to exist.

```bash
cd /d/VibeyReports && dotnet build src/VibeyReports.CrystalWorker -c Debug && dotnet test tests/VibeyReports.Mcp.Tests
```

Expected: PASS, 5 tests.

**If `WorkerLocator_FindsTheCrystalWorkerExecutable` fails:** set the override for the test run and confirm the path is right:

```bash
cd /d/VibeyReports && VIBEY_WORKER_PATH="D:/VibeyReports/src/VibeyReports.CrystalWorker/bin/Debug/net48/VibeyReports.CrystalWorker.exe" dotnet test tests/VibeyReports.Mcp.Tests
```

- [ ] **Step 8: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: add MCP host and Crystal worker process client"
```

---

## Task 10: MCP tools, including PDF-to-PNG preview

**Files:**
- Create: `src/VibeyReports.Mcp/PdfRasterizer.cs`
- Create: `src/VibeyReports.Mcp/ReportTools.cs`
- Modify: `src/VibeyReports.Mcp/VibeyReports.Mcp.csproj` (add the rasteriser package)
- Test: `tests/VibeyReports.Mcp.Tests/ReportToolsTests.cs`

**Interfaces:**
- Consumes: `CrystalWorkerClient` (Task 9), `LayoutPlan`, `VibeyJson.Options`.
- Produces: three MCP tools — `read_report`, `apply_layout`, `preview_report` — plus `PdfRasterizer.FirstPageToPng(byte[] pdf, int dpi) -> byte[]`.

This is where the spec's Phase 6 visual-feedback loop lands: `preview_report` returns the rendered page as MCP **image content**, so Claude sees the actual report and can spot overlaps, bad spacing and misaligned columns without any vision API.

- [ ] **Step 1: Add the rasteriser package**

```bash
cd /d/VibeyReports && dotnet add src/VibeyReports.Mcp package PDFtoImage
```

Record the resolved version that the command prints; it is pinned in the csproj automatically.

- [ ] **Step 2: Write the failing test**

`tests/VibeyReports.Mcp.Tests/ReportToolsTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using VibeyReports.Contracts;
using VibeyReports.Mcp;
using Xunit;

namespace VibeyReports.Mcp.Tests;

public class ReportToolsTests
{
    private static ReportTools Tools() => new ReportTools(new CrystalWorkerClient(WorkerLocator.Find()));

    [Fact]
    public async Task ReadReport_ReturnsSchemaJsonThatDeserialisesBack()
    {
        var json = await Tools().ReadReport(Fixtures.SampleReport, CancellationToken.None);

        var schema = JsonSerializer.Deserialize<ReportSchema>(json, VibeyJson.Options)!;
        schema.Sections.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadReport_ReturnsAReadableErrorForAMissingFile()
    {
        var json = await Tools().ReadReport(@"C:\nope\missing.rpt", CancellationToken.None);

        json.Should().Contain("error");
    }

    [Fact]
    public async Task ApplyLayout_AppliesAValidPlanAndReportsTheOutputPath()
    {
        var schemaJson = await Tools().ReadReport(Fixtures.SampleReport, CancellationToken.None);
        var schema = JsonSerializer.Deserialize<ReportSchema>(schemaJson, VibeyJson.Options)!;
        var target = schema.Sections.SelectMany(s => s.Objects).First(o => o.Kind is "Text" or "Field");

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        var planJson = JsonSerializer.Serialize(new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.SetBold, Target = target.Name, Bold = true } }
        }, VibeyJson.Options);

        try
        {
            var result = await Tools().ApplyLayout(Fixtures.SampleReport, dest, planJson, false, CancellationToken.None);

            result.Should().Contain("\"ok\": true");
            result.Should().Contain("operationsApplied");
            File.Exists(dest).Should().BeTrue();
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task ApplyLayout_ReturnsValidationErrorsForABadPlanAndWritesNothing()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        var planJson = JsonSerializer.Serialize(new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.Move, Target = "NotReal", LeftTwips = 1, TopTwips = 1 } }
        }, VibeyJson.Options);

        var result = await Tools().ApplyLayout(Fixtures.SampleReport, dest, planJson, false, CancellationToken.None);

        result.Should().Contain("\"ok\": false");
        result.Should().Contain("NotReal");
        File.Exists(dest).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyLayout_RejectsMalformedPlanJsonWithAHelpfulMessage()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");

        var result = await Tools().ApplyLayout(Fixtures.SampleReport, dest, "{ not json", false, CancellationToken.None);

        result.Should().Contain("\"ok\": false");
        result.Should().Contain("Could not parse");
    }

    [Fact]
    public void PdfRasterizer_ConvertsAPdfToAPngWithThePngMagicNumber()
    {
        var pdf = File.ReadAllBytes(Path.Combine(Fixtures.Dir, "minimal.pdf"));

        var png = PdfRasterizer.FirstPageToPng(pdf, dpi: 96);

        png.Length.Should().BeGreaterThan(100);
        png[0].Should().Be(0x89);
        png[1].Should().Be((byte)'P');
        png[2].Should().Be((byte)'N');
        png[3].Should().Be((byte)'G');
    }
}
```

- [ ] **Step 3: Create the `minimal.pdf` fixture**

The rasteriser test must not depend on Crystal. Generate a one-page PDF from the worker once and commit it:

```bash
cd /d/VibeyReports && dotnet build src/VibeyReports.CrystalWorker -c Debug && echo '{"command":"render","reportPath":"D:/VibeyReports/tests/fixtures/SampleReport.rpt"}' | ./src/VibeyReports.CrystalWorker/bin/Debug/net48/VibeyReports.CrystalWorker.exe > /tmp/render.json && node -e "const j=require('/tmp/render.json');require('fs').writeFileSync('tests/fixtures/minimal.pdf',Buffer.from(j.pdfBase64,'base64'))" 2>/dev/null || powershell -NoProfile -Command "\$j = Get-Content /tmp/render.json -Raw | ConvertFrom-Json; [IO.File]::WriteAllBytes('D:\VibeyReports\tests\fixtures\minimal.pdf', [Convert]::FromBase64String(\$j.pdfBase64))"
```

Verify it is a real PDF:

```bash
cd /d/VibeyReports && head -c 5 tests/fixtures/minimal.pdf && echo "" && ls -la tests/fixtures/minimal.pdf
```

Expected: prints `%PDF-` and a non-zero size.

- [ ] **Step 4: Run the tests to verify they fail**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Mcp.Tests --filter ReportToolsTests
```

Expected: FAIL — `The type or namespace name 'ReportTools' could not be found`.

- [ ] **Step 5: Write `PdfRasterizer.cs`**

```csharp
using PDFtoImage;
using SkiaSharp;

namespace VibeyReports.Mcp;

public static class PdfRasterizer
{
    /// <summary>Renders page 1 of a PDF to PNG bytes. 96 dpi keeps previews small enough to send inline.</summary>
    public static byte[] FirstPageToPng(byte[] pdfBytes, int dpi = 96)
    {
        if (pdfBytes is null || pdfBytes.Length == 0) throw new ArgumentException("PDF bytes are empty.", nameof(pdfBytes));

        using var bitmap = Conversion.ToImage(pdfBytes, page: 0, options: new RenderOptions(Dpi: dpi));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);

        return data.ToArray();
    }
}
```

- [ ] **Step 6: Write `ReportTools.cs`**

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VibeyReports.Contracts;

namespace VibeyReports.Mcp;

[McpServerToolType]
public sealed class ReportTools
{
    private readonly CrystalWorkerClient _worker;

    public ReportTools(CrystalWorkerClient worker) => _worker = worker;

    [McpServerTool(Name = "read_report")]
    [Description("""
        Read a Crystal Reports XI R2 .rpt file and return its layout as JSON: page size and
        margins in twips, every section with its height, and every object with its name, kind,
        position, size, font and alignment. Object and section names in this result are the
        identifiers you must use in a layout plan. 1440 twips = 1 inch.
        """)]
    public async Task<string> ReadReport(
        [Description("Absolute path to the .rpt file to read.")] string reportPath,
        CancellationToken cancellationToken)
    {
        var response = await _worker.ReadAsync(reportPath, cancellationToken);

        if (!response.Ok || response.Schema is null)
            return Fail(response.Error ?? "The worker could not read the report.", response.ValidationErrors);

        return JsonSerializer.Serialize(response.Schema, VibeyJson.Options);
    }

    [McpServerTool(Name = "apply_layout")]
    [Description("""
        Apply a layout plan to a .rpt file and save the result as a NEW .rpt that opens in
        Crystal Reports XI R2. The plan is JSON: {"planVersion":1,"operations":[...]}.
        Supported actions: move, resize, setFont, setFontSize, setBold, setAlignment,
        addText, addLine, addBox, resizeSection.
        move/resize/setFont/setFontSize/setBold/setAlignment take "target" (an object name).
        addText/addLine/addBox/resizeSection take "section" (a section name); the add actions
        also take "newName" and full geometry. All coordinates are twips (1440 = 1 inch).
        Layout only: database connections, SQL, formulas, parameters, record selection and
        grouping cannot be changed and any attempt is rejected. The whole plan is validated
        before anything is written, so a rejected plan leaves no output file behind.
        """)]
    public async Task<string> ApplyLayout(
        [Description("Absolute path to the source .rpt file. Never modified.")] string reportPath,
        [Description("Absolute path for the new .rpt file to create.")] string outputPath,
        [Description("The layout plan as a JSON string.")] string planJson,
        [Description("Set true to replace an existing file at outputPath.")] bool overwrite,
        CancellationToken cancellationToken)
    {
        LayoutPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<LayoutPlan>(planJson, VibeyJson.Options);
        }
        catch (JsonException ex)
        {
            return Fail($"Could not parse the layout plan as JSON: {ex.Message}");
        }

        if (plan is null) return Fail("Could not parse the layout plan: it deserialised to null.");

        var response = await _worker.ApplyAsync(reportPath, outputPath, plan, overwrite, cancellationToken);

        if (!response.Ok)
            return Fail(response.Error ?? "The worker could not apply the plan.", response.ValidationErrors);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            operationsApplied = response.OperationsApplied,
            outputPath = response.OutputPath,
            schema = response.Schema
        }, VibeyJson.Options);
    }

    [McpServerTool(Name = "preview_report")]
    [Description("""
        Render the first page of a .rpt file and return it as an image so you can see the
        actual laid-out report. Use this after apply_layout to check for overlapping fields,
        uneven spacing, misaligned columns, oversized headings and inconsistent margins, then
        issue a corrected layout plan.
        """)]
    public async Task<CallToolResult> PreviewReport(
        [Description("Absolute path to the .rpt file to render.")] string reportPath,
        [Description("Render resolution in DPI. 96 is usually enough; use 150 for fine detail.")] int dpi,
        CancellationToken cancellationToken)
    {
        var response = await _worker.RenderAsync(reportPath, cancellationToken);

        if (!response.Ok || string.IsNullOrWhiteSpace(response.PdfBase64))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = response.Error ?? "The worker could not render the report." }]
            };
        }

        byte[] png;
        try
        {
            png = PdfRasterizer.FirstPageToPng(Convert.FromBase64String(response.PdfBase64), dpi <= 0 ? 96 : dpi);
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"Rendered the PDF but could not rasterise it: {ex.Message}" }]
            };
        }

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = $"Page 1 of {Path.GetFileName(reportPath)} at {(dpi <= 0 ? 96 : dpi)} dpi." },
                new ImageContentBlock { Data = Convert.ToBase64String(png), MimeType = "image/png" }
            ]
        };
    }

    private static string Fail(string error, List<ValidationError>? validationErrors = null) =>
        JsonSerializer.Serialize(new { ok = false, error, validationErrors }, VibeyJson.Options);
}
```

- [ ] **Step 7: Run the tests**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Mcp.Tests
```

Expected: PASS, 11 tests.

**If `ModelContextProtocol.Protocol` types do not resolve** (`CallToolResult`, `TextContentBlock`, `ImageContentBlock`): the 1.2.0 namespace layout differs. Find the real names:

```bash
cd /d/VibeyReports && grep -rl "ContentBlock" ~/.nuget/packages/modelcontextprotocol/1.2.0/lib/ 2>/dev/null; ls ~/.nuget/packages/modelcontextprotocol/1.2.0/lib/
```

Then adjust the `using` and type names. Keep the tool returning image content — that is the point of the task.

**If `Conversion.ToImage` has a different signature:** check the installed PDFtoImage version's API with `ls ~/.nuget/packages/pdftoimage/`. The call must render page index 0 at the requested DPI.

- [ ] **Step 8: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: expose read_report, apply_layout and preview_report MCP tools"
```

---

## Task 11: Publish, register with Claude Code, and verify the MVP success criteria

**Files:**
- Create: `D:\VibeyReports\publish.ps1`
- Create: `D:\VibeyReports\README.md`
- Modify: `C:\Users\avin.a\.claude.json` (add the `vibey-reports` MCP server)

**Interfaces:**
- Consumes: the published `VibeyReports.Mcp.exe` and `VibeyReports.CrystalWorker.exe`.
- Produces: a registered MCP server named `vibey-reports` exposing `read_report`, `apply_layout`, `preview_report`.

- [ ] **Step 1: Write `publish.ps1`**

The worker must land next to the MCP exe so `WorkerLocator` finds it side-by-side with no environment variable.

```powershell
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDir = "$PSScriptRoot\dist"
)

$ErrorActionPreference = 'Stop'

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null

Write-Host "Publishing MCP server (net10.0)..."
dotnet publish "$PSScriptRoot\src\VibeyReports.Mcp" -c Release -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "MCP publish failed." }

Write-Host "Publishing Crystal worker (net48, x86)..."
dotnet publish "$PSScriptRoot\src\VibeyReports.CrystalWorker" -c Release -r win-x86 --self-contained false -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed." }

$mcp    = Join-Path $OutputDir 'VibeyReports.Mcp.exe'
$worker = Join-Path $OutputDir 'VibeyReports.CrystalWorker.exe'

if (-not (Test-Path $mcp))    { throw "Missing $mcp" }
if (-not (Test-Path $worker)) { throw "Missing $worker" }

Write-Host ""
Write-Host "Published to $OutputDir"
Write-Host "  MCP server: $mcp"
Write-Host "  Worker:     $worker"
```

- [ ] **Step 2: Run the publish and verify both executables exist**

```bash
cd /d/VibeyReports && powershell -NoProfile -ExecutionPolicy Bypass -File ./publish.ps1 && ls -la dist/VibeyReports.Mcp.exe dist/VibeyReports.CrystalWorker.exe
```

Expected: both files listed, non-zero size.

- [ ] **Step 3: Verify the published worker answers the protocol directly**

```bash
cd /d/VibeyReports && echo '{"command":"read","reportPath":"D:/VibeyReports/tests/fixtures/SampleReport.rpt"}' | ./dist/VibeyReports.CrystalWorker.exe | head -c 400
```

Expected: JSON beginning `{ "ok": true, "schema": { "reportPath": ...`.

- [ ] **Step 4: Run the whole test suite one more time**

```bash
cd /d/VibeyReports && dotnet test
```

Expected: PASS. 3 test projects, 35 tests total, 0 failed.

- [ ] **Step 5: Register the MCP server with Claude Code**

Add the `vibey-reports` entry to the root `mcpServers` object in `C:\Users\avin.a\.claude.json`, alongside `tcm-testcases` and `phr-db-mcp`:

```json
"vibey-reports": {
  "command": "D:\\VibeyReports\\dist\\VibeyReports.Mcp.exe",
  "args": []
}
```

Apply it with a script rather than by hand so the rest of the file is untouched:

```powershell
$path = "$env:USERPROFILE\.claude.json"
Copy-Item $path "$path.bak" -Force
$cfg = Get-Content $path -Raw | ConvertFrom-Json
$entry = [PSCustomObject]@{ command = 'D:\VibeyReports\dist\VibeyReports.Mcp.exe'; args = @() }
$cfg.mcpServers | Add-Member -NotePropertyName 'vibey-reports' -NotePropertyValue $entry -Force
$cfg | ConvertTo-Json -Depth 30 | Set-Content $path -Encoding utf8
Write-Host "Registered. Backup at $path.bak"
```

- [ ] **Step 6: Restart Claude Code and confirm the three tools are listed**

In a new Claude Code session, confirm `read_report`, `apply_layout` and `preview_report` appear under `vibey-reports`. If the server fails to start, run the exe directly — it should sit waiting on stdin rather than exiting:

```bash
cd /d/VibeyReports && ./dist/VibeyReports.Mcp.exe < /dev/null
```

- [ ] **Step 7: Walk the MVP success criteria end to end**

Drive this from Claude Code against `D:\VibeyReports\tests\fixtures\PMSV10_IndPerfOverview.rpt`, which is a real PeoplesHR report:

1. `read_report` returns its schema. ✔ spec criterion 1–2
2. Claude proposes a `LayoutPlan` from that schema plus a design instruction. ✔ criterion 3–4
3. `apply_layout` writes `D:\VibeyReports\out\PMSV10_IndPerfOverview.vibey.rpt`. ✔ criterion 5–6
4. `preview_report` on the output returns a PNG Claude can inspect. ✔ spec Phase 6
5. **Open the generated `.rpt` in Crystal Reports XI R2 by hand and confirm it loads.** ✔ criterion 7

Step 5 is manual and is the real gate. Record the outcome in the README.

- [ ] **Step 8: Write `README.md`**

```markdown
# Vibey Reports

AI-assisted layout redesign for Crystal Reports XI R2 `.rpt` files, driven from Claude Code
over MCP. Reads a report's layout, applies a validated layout plan, saves a new `.rpt`, and
renders a preview image so Claude can review its own work.

## Why the odd shape

Crystal XI R2's in-process RAS SDK is .NET 2.0, x86 and COM-bound. It lives in
`VibeyReports.CrystalWorker` (net48, x86) and nowhere else. `VibeyReports.Mcp` (net10.0)
talks to it as a child process over JSON on stdin/stdout.

**The Crystal assemblies must come from
`C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2` (version 11.5).**
The `CrystalReports.*` 13.x NuGet packages will open XI R2 files but save them in a newer
format that XI R2 and PeoplesHR cannot open.

## Build and publish

```powershell
.\publish.ps1
```

## Register with Claude Code

Add to `mcpServers` in `~/.claude.json`:

```json
"vibey-reports": {
  "command": "D:\\VibeyReports\\dist\\VibeyReports.Mcp.exe",
  "args": []
}
```

## Tools

| Tool | Purpose |
|---|---|
| `read_report` | `.rpt` → layout JSON (twips, sections, objects, fonts) |
| `apply_layout` | layout plan → new `.rpt` |
| `preview_report` | `.rpt` → rendered PNG of page 1 |

## Supported operations

`move`, `resize`, `setFont`, `setFontSize`, `setBold`, `setAlignment`, `addText`, `addLine`,
`addBox`, `resizeSection`.

Layout only. Database connections, SQL, formulas, parameters, record selection and grouping
cannot be changed; `LayoutPlanValidator` rejects any attempt.

## Tests

```bash
dotnet test
```

`VibeyReports.CrystalWorker.Tests` and `VibeyReports.Mcp.Tests` need Crystal Reports XI R2
installed. `VibeyReports.Contracts.Tests` runs anywhere.

## MVP verification

<!-- Fill in after Task 11 Step 7. -->
- [ ] Generated `.rpt` opens in Crystal Reports XI R2 — date, report used, result.
```

- [ ] **Step 9: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: publish script, MCP registration and README"
```

---

## Deferred (explicitly out of scope for v1)

Per the spec's "Do not initially support", and not covered by any task above:

- Charts, crosstabs, subreport internals, pictures
- Conditional formatting and formula creation
- Database, parameter, grouping and sorting changes
- Colour changes (font colour, fill colour, borders) — the DTOs carry no colour field yet
- Multi-page previews (`preview_report` renders page 1 only)
- A "golden template" style library that conforms many reports to one prototype

Add these only once the MVP loop in Task 11 Step 7 is reliable. The natural first additions
are colour operations (small: extend `ObjectInfo`, `LayoutOperation`, the validator and the
applier) and multi-page preview (small: add a `page` parameter to `preview_report`).

---

## Self-Review

**Spec coverage.** Spec Phase 1 → Tasks 4–5 and 7. Phase 2 (intermediate JSON) → Task 1. Phase 3 (restricted command model) → Tasks 2–3; all ten named operations are present in `LayoutActions.All` and tested. Phase 4 (writer) → Task 6, with the forbidden-change list enforced in Task 3's validator rather than left to convention. Phase 5 (AI layout generation) → Task 10; the AI is Claude Code itself via MCP, which is the user's no-API-key answer to this phase. Phase 6 (preview and visual feedback) → Task 10's `preview_report`; the refinement loop is Claude re-issuing `apply_layout`, and it is bounded by the user rather than a hard pass limit — a deliberate change from the spec, since there is no autonomous loop to cap. Phase 7 (separate legacy code) → the three-project split, with the JSON-over-stdio transport the spec listed first. All seven MVP success criteria are walked in Task 11 Step 7, with criterion 7 (opens in XI R2) flagged as the manual gate it has to be.

**Two spec deviations, both deliberate.** The spec's `/DesignerApp` modern UI is replaced by Claude Code itself — the user cannot use an API key, so the app cannot call an AI; inverting it so the AI calls the app removes the blocker and the UI at once. And "limit the number of automatic refinement passes" has no code, because with MCP the human is in the loop on every pass.

**Placeholders.** None. Every code step carries real code, every test step names a command and its expected result, and the three places where the legacy SDK could surprise us (`ByteArray.get_Stream`, `ParagraphElements.Add`, section-band classification) carry concrete recovery instructions with a runnable reflection command rather than "handle errors appropriately".

**Type consistency.** `ReportSchema`/`PageInfo`/`SectionInfo`/`ObjectInfo` (Task 1) are consumed unchanged in Tasks 3, 5, 8. `LayoutPlan`/`LayoutOperation`/`LayoutActions` (Task 2) flow through Tasks 3, 6, 8, 9, 10. `LayoutPlanValidator.Validate(plan, schema)` is defined in Task 3 and called with that exact signature in Task 6. `CrystalSession.Open`/`SaveAs`/`Document` (Task 4) are used verbatim in Tasks 5, 6, 7, 8. `ReportReader.Read(session)` (Task 5) is called in Tasks 6 and 8. `LayoutApplier.Apply(session, plan)` and `InvalidPlanException.Result` (Task 6) are caught by name in Task 8. `WorkerRequest`/`WorkerResponse`/`WorkerCommands` (Task 8) are bound by `CrystalWorkerClient` (Task 9) and surfaced by `ReportTools` (Task 10). `CrystalWorkerClient.ReadAsync`/`ApplyAsync`/`RenderAsync` are declared in Task 9 and called in Task 10. All geometry properties are `*Twips` everywhere.

**One known soft spot.** Task 5's `ClassifySection` guesses the band from the section name. RAS may well name sections `Section1`, `Section2`… with the band living on the parent `Area` instead. The test asserts the correct outcome and Step 4 carries the exact fix if it fails, so this surfaces as a red test rather than silently wrong data.

---

# Addendum A — `addField` (approved scope change, 2026-08-31)

The user approved adding a bound-database-field operation after reviewing a target layout
(a four-column table: bold headings in a header band, `stage_name` / `stage_period` /
`stage_status` / `stage_outcome_score` in Details). Without `addField`, Vibey Reports can arrange
those fields but cannot create them, so the layout is only reachable from a pre-seeded `.rpt`.

**This does not weaken Global Constraint 5.** `addField` cannot add tables, change a connection,
alter SQL, or touch table links. It may only reference a field **already present in the report's
existing data source**, and the validator rejects any `fieldRef` that is not in the report's own
field list. Nothing about the data source changes — only which of its existing fields are placed
on the canvas.

## Verified SDK surface (reflected from the installed 11.5 assemblies)

- `doc.DatabaseController.Database.Tables` → collection of `ISCRTable`
- `ISCRTable.DataFields` → collection of `ISCRDBField`
- `ISCRDBField`: `Name`, **`FormulaForm`** (the `{Table.Field}` expression), `TableAlias`,
  `Type` (`CrFieldValueTypeEnum`), `Kind` (`CrFieldKindEnum`), `HeadingText`, `LongName`
- `ISCRFieldObject.DataSource` (String) — set this to a `FormulaForm` value to bind
- `ReportObjectController.Add(ISCRReportObject, Section, int)` — same call the other adds use
- `ReportObjectController.AddByName(String fieldName, String headingText)` — fallback only; it
  chooses its own section and position, so it cannot satisfy a positioned layout plan

`FormulaForm` is the join between reading and writing: `ObjectInfo.DataSource` already reports it
for existing fields, so a plan can reference an existing field and a new one identically.

---

## Amendment to Task 5 — also extract the available field list

Task 5's `ReportReader` gains one more responsibility. The spec's Phase 5 says the AI must be sent
"Available fields"; the original plan dropped that, which is precisely why `addField` was
unreachable. Restore it.

**Additional files:** none — extend `src/VibeyReports.Contracts/ReportSchema.cs` and
`src/VibeyReports.CrystalWorker/ReportReader.cs`.

**Additional interface produced:** `ReportSchema.AvailableFields` (`List<FieldInfo>`), and:

```csharp
public sealed class FieldInfo
{
    /// <summary>Raw field name, e.g. "stage_name".</summary>
    public string Name { get; set; } = "";
    /// <summary>The bindable expression, e.g. "{Command.stage_name}". Use THIS as addField's fieldRef.</summary>
    public string FormulaForm { get; set; } = "";
    public string TableAlias { get; set; } = "";
    /// <summary>String, Number, Currency, DateTime, Date, Time, Boolean, Blob, Other.</summary>
    public string ValueType { get; set; } = "";
    /// <summary>Crystal's default heading for this field, useful as addText content.</summary>
    public string HeadingText { get; set; } = "";
}
```

Extraction, inside `ReportReader.Read`, after the sections loop:

```csharp
var tables = doc.DatabaseController.Database.Tables;
for (var t = 0; t < tables.Count; t++)
{
    var table = tables[t];
    var fields = table.DataFields;
    for (var f = 0; f < fields.Count; f++)
    {
        var dbField = (ISCRDBField)fields[f];
        schema.AvailableFields.Add(new FieldInfo
        {
            Name = dbField.Name ?? "",
            FormulaForm = dbField.FormulaForm ?? "",
            TableAlias = dbField.TableAlias ?? "",
            ValueType = ClassifyValueType(dbField.Type),
            HeadingText = dbField.HeadingText ?? ""
        });
    }
}
```

with:

```csharp
private static string ClassifyValueType(CrFieldValueTypeEnum type)
{
    switch (type)
    {
        case CrFieldValueTypeEnum.crFieldValueTypeStringField: return "String";
        case CrFieldValueTypeEnum.crFieldValueTypeInt8sField:
        case CrFieldValueTypeEnum.crFieldValueTypeInt8uField:
        case CrFieldValueTypeEnum.crFieldValueTypeInt16sField:
        case CrFieldValueTypeEnum.crFieldValueTypeInt16uField:
        case CrFieldValueTypeEnum.crFieldValueTypeInt32sField:
        case CrFieldValueTypeEnum.crFieldValueTypeInt32uField:
        case CrFieldValueTypeEnum.crFieldValueTypeNumberField: return "Number";
        case CrFieldValueTypeEnum.crFieldValueTypeCurrencyField: return "Currency";
        case CrFieldValueTypeEnum.crFieldValueTypeDateField: return "Date";
        case CrFieldValueTypeEnum.crFieldValueTypeTimeField: return "Time";
        case CrFieldValueTypeEnum.crFieldValueTypeDateTimeField: return "DateTime";
        case CrFieldValueTypeEnum.crFieldValueTypeBooleanField: return "Boolean";
        case CrFieldValueTypeEnum.crFieldValueTypeBlobField: return "Blob";
        default: return "Other";
    }
}
```

The exact `CrFieldValueTypeEnum` member names must be confirmed against the installed assembly; if
a member above does not exist, keep the mapping shape and drop or rename that case. The reader must
compile against the real enum, not this listing.

**Additional tests for Task 5:**

```csharp
[Fact]
public void Read_ReturnsAvailableFieldsWithBindableFormulaForms()
{
    using var session = CrystalSession.Open(Fixtures.SampleReport);

    var schema = ReportReader.Read(session);

    schema.AvailableFields.Should().NotBeEmpty();
    schema.AvailableFields.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Name));
    schema.AvailableFields.Should().OnlyContain(f => f.FormulaForm.StartsWith("{") && f.FormulaForm.EndsWith("}"));
}

[Fact]
public void Read_AvailableFieldsCoverTheDataSourcesOfPlacedFieldObjects()
{
    using var session = CrystalSession.Open(Fixtures.SampleReport);

    var schema = ReportReader.Read(session);
    var placed = schema.Sections.SelectMany(s => s.Objects)
                       .Where(o => o.Kind == "Field" && !string.IsNullOrWhiteSpace(o.DataSource))
                       .Select(o => o.DataSource!)
                       .ToList();

    // Every already-placed database field should be referenceable by addField.
    // Formula and special fields legitimately are not, so this asserts overlap, not containment.
    if (placed.Count > 0)
    {
        placed.Any(p => schema.AvailableFields.Any(f => f.FormulaForm == p))
              .Should().BeTrue();
    }
}
```

**Note for the implementer:** enumerating tables must not trigger a database logon. If it does on a
fixture, catch the failure and leave `AvailableFields` empty for that report rather than failing the
read — reading layout must never require a live connection. Add a test asserting `Read` still
succeeds in that case.

---

## Task 6b: `addField`

Run this AFTER Task 6 is complete and reviewed. It touches three already-committed files.

**Files:**
- Modify: `src/VibeyReports.Contracts/LayoutPlan.cs` (add the constant and `FieldRef`)
- Modify: `src/VibeyReports.Contracts/LayoutPlanValidator.cs` (add the validation branch)
- Modify: `src/VibeyReports.CrystalWorker/LayoutApplier.cs` (add the switch arm)
- Test: `tests/VibeyReports.Contracts.Tests/LayoutPlanValidatorTests.cs` (extend)
- Test: `tests/VibeyReports.CrystalWorker.Tests/LayoutApplierTests.cs` (extend)

**Interfaces:**
- Consumes: `ReportSchema.AvailableFields` / `FieldInfo` (Task 5 amendment), `CrystalSession`,
  `ReportReader.Read`, everything from Tasks 2, 3 and 6.
- Produces: `LayoutActions.AddField` (`"addField"`), `LayoutOperation.FieldRef`, and an eleventh
  applier arm. Task 10's `apply_layout` description must list it.

- [ ] **Step 1: Write the failing validator tests**

Add to `LayoutPlanValidatorTests.cs`. Extend the shared `Schema()` factory with an available field
so these can bind:

```csharp
// inside Schema(), alongside Sections:
AvailableFields =
{
    new FieldInfo { Name = "stage_name", FormulaForm = "{Command.stage_name}",
                    TableAlias = "Command", ValueType = "String", HeadingText = "Stage Name" }
}
```

```csharp
[Fact]
public void Validate_AcceptsAddFieldReferencingAnAvailableField()
{
    var plan = PlanOf(new LayoutOperation
    {
        Action = LayoutActions.AddField, Section = "Section3", NewName = "fStageName",
        FieldRef = "{Command.stage_name}",
        LeftTwips = 0, TopTwips = 20, WidthTwips = 2500, HeightTwips = 260
    });

    var result = LayoutPlanValidator.Validate(plan, Schema());

    result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
}

[Fact]
public void Validate_RejectsAddFieldReferencingAFieldNotInTheReportsDataSource()
{
    var plan = PlanOf(new LayoutOperation
    {
        Action = LayoutActions.AddField, Section = "Section3", NewName = "fBogus",
        FieldRef = "{Command.salary_secret}",
        LeftTwips = 0, TopTwips = 20, WidthTwips = 2500, HeightTwips = 260
    });

    var result = LayoutPlanValidator.Validate(plan, Schema());

    result.IsValid.Should().BeFalse();
    result.Errors[0].Message.Should().Contain("salary_secret");
    result.Errors[0].Message.Should().Contain("not a field in this report");
}

[Fact]
public void Validate_RejectsAddFieldWithoutAFieldRef()
{
    var plan = PlanOf(new LayoutOperation
    {
        Action = LayoutActions.AddField, Section = "Section3", NewName = "fNoRef",
        LeftTwips = 0, TopTwips = 20, WidthTwips = 2500, HeightTwips = 260
    });

    var result = LayoutPlanValidator.Validate(plan, Schema());

    result.IsValid.Should().BeFalse();
    result.Errors[0].Message.Should().Contain("fieldRef");
}

[Fact]
public void Validate_AllowsFontOperationsOnAFieldAddedEarlierInThePlan()
{
    // addField creates a Kind="Field" object, which IS fontable. Guards against an F1 regression.
    var plan = PlanOf(
        new LayoutOperation
        {
            Action = LayoutActions.AddField, Section = "Section3", NewName = "fStageName",
            FieldRef = "{Command.stage_name}",
            LeftTwips = 0, TopTwips = 20, WidthTwips = 2500, HeightTwips = 260
        },
        new LayoutOperation { Action = LayoutActions.SetBold, Target = "fStageName", Bold = true });

    var result = LayoutPlanValidator.Validate(plan, Schema());

    result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
}

[Fact]
public void Validate_RejectsAddFieldWhoseNewNameCollides()
{
    var plan = PlanOf(new LayoutOperation
    {
        Action = LayoutActions.AddField, Section = "Section3", NewName = "CustomerName",
        FieldRef = "{Command.stage_name}",
        LeftTwips = 0, TopTwips = 20, WidthTwips = 2500, HeightTwips = 260
    });

    var result = LayoutPlanValidator.Validate(plan, Schema());

    result.IsValid.Should().BeFalse();
    result.Errors[0].Message.Should().Contain("already exists");
}
```

- [ ] **Step 2: Run them to verify they fail**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Contracts.Tests --filter LayoutPlanValidatorTests
```

Expected: FAIL — `LayoutActions.AddField` and `LayoutOperation.FieldRef` do not exist.

- [ ] **Step 3: Extend `LayoutPlan.cs`**

```csharp
// in LayoutActions:
public const string AddField = "addField";

// and append AddField to the All array:
public static readonly string[] All =
{
    Move, Resize, SetFont, SetFontSize, SetBold,
    SetAlignment, AddText, AddLine, AddBox, ResizeSection, AddField
};

// in LayoutOperation:
/// <summary>
/// Bindable field expression for addField, e.g. "{Command.stage_name}".
/// Must exactly match a ReportSchema.AvailableFields[].FormulaForm.
/// </summary>
public string? FieldRef { get; set; }
```

Note: the existing test `LayoutActions_All_ContainsExactlyTheTenMvpActions` will now fail because
`All` has eleven entries. Update that test to expect eleven including `addField`, and rename it
accordingly. This is the one pre-existing test you are authorised to change.

- [ ] **Step 4: Extend `LayoutPlanValidator.cs`**

`addField` joins the `needsSection` set and the add-branch geometry rules, with one extra check.

```csharp
// widen needsSection:
var needsSection = action is LayoutActions.AddText or LayoutActions.AddLine
    or LayoutActions.AddBox or LayoutActions.ResizeSection or LayoutActions.AddField;

// add AddField to the shared add case label:
case LayoutActions.AddText:
case LayoutActions.AddLine:
case LayoutActions.AddBox:
case LayoutActions.AddField:
{
    // ... existing newName / collision / geometry checks unchanged ...

    if (action == LayoutActions.AddField)
    {
        if (string.IsNullOrWhiteSpace(op.FieldRef))
        {
            Err("\"addField\" requires \"fieldRef\".");
            return errs;
        }
        if (!availableFieldRefs.Contains(op.FieldRef!))
        {
            Err($"\"{op.FieldRef}\" is not a field in this report's data source. " +
                "Use one of the formulaForm values from the report schema's availableFields.");
            return errs;
        }
    }

    // ... existing bounds checks unchanged ...

    if (errs.Count == 0)
        objects[op.NewName!] = new SimObject
        {
            Section = op.Section!, Kind = KindForAdd(action),
            Left = l, Top = t, Width = w, Height = h
        };
    break;
}
```

Build `availableFieldRefs` alongside the other simulation state at the top of `Validate`:

```csharp
var availableFieldRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var f in schema.AvailableFields ?? new List<FieldInfo>())
    if (!string.IsNullOrWhiteSpace(f.FormulaForm)) availableFieldRefs.Add(f.FormulaForm);
```

and extend the kind helper introduced in the round-1 fixes so an added field is fontable:

```csharp
private static string KindForAdd(string action) =>
    action == LayoutActions.AddText  ? "Text"
  : action == LayoutActions.AddLine  ? "Line"
  : action == LayoutActions.AddField ? "Field"
  : "Box";
```

If the round-1 fix inlined this mapping rather than extracting a helper, add the `AddField` arm
wherever that mapping lives.

- [ ] **Step 5: Run the validator tests**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.Contracts.Tests
```

Expected: PASS, 0 failed. Every pre-existing test must still pass — in particular the F1 regression
test, which asserts font operations are rejected on `Kind = "Line"`.

- [ ] **Step 6: Write the failing applier test**

Add to `LayoutApplierTests.cs`:

```csharp
[Fact]
public void Apply_AddsABoundFieldWhoseDataSourceSurvivesSaveAndReopen()
{
    string sectionName;
    string fieldRef;
    using (var s = CrystalSession.Open(Fixtures.SampleReport))
    {
        var schema = ReportReader.Read(s);
        sectionName = schema.Sections.First(x => x.Kind == "Details" && x.HeightTwips >= 260).Name;
        fieldRef = schema.AvailableFields.First().FormulaForm;
    }

    var plan = new LayoutPlan
    {
        Operations =
        {
            new LayoutOperation
            {
                Action = LayoutActions.AddField, Section = sectionName, NewName = "VibeyField",
                FieldRef = fieldRef,
                LeftTwips = 0, TopTwips = 0, WidthTwips = 2500, HeightTwips = 240
            }
        }
    };

    var schemaAfter = ApplyAndReread(plan, out var saved);
    try
    {
        var added = schemaAfter.Sections.SelectMany(s => s.Objects)
                               .SingleOrDefault(o => o.Name == "VibeyField");
        added.Should().NotBeNull();
        added!.Kind.Should().Be("Field");
        added.DataSource.Should().Be(fieldRef);
        added.WidthTwips.Should().Be(2500);
    }
    finally { if (File.Exists(saved)) File.Delete(saved); }
}
```

If `SampleReport.rpt` turns out to have no Details section at least 260 twips tall, or no available
fields, switch the fixture to one that does and say which in the report.

- [ ] **Step 7: Run it to verify it fails**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests --filter Apply_AddsABoundField
```

Expected: FAIL — the applier throws `Unhandled action "addField"`.

- [ ] **Step 8: Extend `LayoutApplier.cs`**

```csharp
case LayoutActions.AddField:
    AddField(doc, op);
    break;
```

```csharp
private static void AddField(
    CrystalDecisions.ReportAppServer.ClientDoc.ISCDReportClientDocument doc,
    LayoutOperation op)
{
    var section = FindSection(doc, op.Section);

    var field = new FieldObjectClass
    {
        Name = op.NewName,
        DataSource = op.FieldRef,
        Left = op.LeftTwips.Value,
        Top = op.TopTwips.Value,
        Width = op.WidthTwips.Value,
        Height = op.HeightTwips.Value
    };

    doc.ReportDefController.ReportObjectController.Add(field, section, -1);
}
```

- [ ] **Step 9: Run the applier tests**

```bash
cd /d/VibeyReports && dotnet test tests/VibeyReports.CrystalWorker.Tests
```

Expected: PASS, 0 failed.

**If `Add` throws because `DataSource` is not resolvable:** the field expression must be bound to a
field the report's data context actually knows. Confirm `op.FieldRef` came from
`AvailableFields[].FormulaForm` for THIS report. If direct construction still fails, fall back to
`ReportObjectController.AddByName(fieldName, headingText)` — call it, then locate the created object
via `GetAllReportObjects()` and apply the requested position with the existing `ModifyObject`
helper. Record which path was used in the report; the test's assertions do not change either way.

**If `DataSource` comes back empty after reopen:** the binding did not persist. Do not weaken the
test — persistence is the one thing `addField` exists to guarantee. Try `AddByName` as above.

- [ ] **Step 10: Update Task 10's `apply_layout` description**

Add `addField` to the tool's supported-actions list and document `fieldRef`:

> `addField` places a bound database field. It takes `section`, `newName`, `fieldRef` and full
> geometry. `fieldRef` MUST be one of the `formulaForm` values from the schema's `availableFields`
> — it can only reference fields already in the report's data source, and cannot add tables or
> change any connection.

If Task 10 is already implemented when this runs, update the `[Description]` attribute in
`ReportTools.cs` directly.

- [ ] **Step 11: Commit**

```bash
cd /d/VibeyReports && git add -A && git commit -m "feat: add addField operation for placing bound database fields"
```

---

## Updated deferred list

`addField` moves OUT of deferred. Still deferred: charts, crosstabs, subreport internals, pictures,
conditional formatting, formula creation, database/table/connection changes, parameter and grouping
changes, colour operations, multi-page preview, and the golden-template style library.
