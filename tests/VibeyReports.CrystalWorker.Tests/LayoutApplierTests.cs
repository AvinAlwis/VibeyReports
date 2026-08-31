using System;
using System.IO;
using System.Linq;
using CrystalDecisions.ReportAppServer.ReportDefModel;
using FluentAssertions;
using VibeyReports.Contracts;
using VibeyReports.CrystalWorker;
using Xunit;

namespace VibeyReports.CrystalWorker.Tests;

public class LayoutApplierTests
{
    private static string TempRpt() => Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");

    /// <summary>
    /// Applies a plan, saves, reopens, and hands back the resulting schema. T3: also asserts the
    /// source .rpt was never touched -- "the source must never be modified" is a global constraint,
    /// and every test in this file mutates a session opened directly on Fixtures.SampleReport, so
    /// checking it here covers all of them for free.
    /// </summary>
    private static ReportSchema ApplyAndReread(LayoutPlan plan, out string savedPath, string sourcePath = null)
    {
        sourcePath = sourcePath ?? Fixtures.SampleReport;
        var sourceBefore = File.ReadAllBytes(sourcePath);

        var dest = TempRpt();
        using (var session = CrystalSession.Open(sourcePath))
        {
            LayoutApplier.Apply(session, plan);
            session.SaveAs(dest, overwrite: false);
        }

        File.ReadAllBytes(sourcePath).Should().Equal(sourceBefore,
            because: "the source report must never be modified");

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

    /// <summary>
    /// Measured against SampleReport.rpt: every Text/Field object on it sits flush against the
    /// bottom of its own section (Top + Height == section HeightTwips exactly, on every field in
    /// every section), so growing ANY of them in place -- e.g. the unconditional "first Text/Field
    /// object", PrintDate1, in a 442-twip PageHeaderSection1 -- always fails the validator's
    /// section-bounds check. That is the validator doing its job, not a bug in the applier; the
    /// fixture just leaves zero headroom below any placed field. To exercise a real resize-and-
    /// persist against this fixture, free up room first: move the object to the top of its section
    /// (topTwips = 0), then resize into the space that opens up below it.
    /// </summary>
    private static string FirstTextOrFieldNameAndItsLeft(out int leftTwips)
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);
        var schema = ReportReader.Read(session);
        var found = schema.Sections
            .SelectMany(s => s.Objects)
            .First(o => o.Kind == "Text" || o.Kind == "Field");
        leftTwips = found.LeftTwips;
        return found.Name;
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
        var name = FirstTextOrFieldNameAndItsLeft(out var left);
        var plan = new LayoutPlan
        {
            Operations =
            {
                // Free up vertical room first: every field on this fixture starts flush against
                // its section's bottom edge, so a plain resize-taller would fail validation.
                new LayoutOperation { Action = LayoutActions.Move, Target = name, LeftTwips = left, TopTwips = 0 },
                new LayoutOperation { Action = LayoutActions.Resize, Target = name, WidthTwips = 1440, HeightTwips = 240 }
            }
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

    [Fact]
    public void Apply_MovingALineKeepsItsEndpointsConsistent()
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
                    Action = LayoutActions.AddLine, Section = sectionName, NewName = "EndpointRule",
                    LeftTwips = 0, TopTwips = 100, WidthTwips = 2880, HeightTwips = 0
                },
                new LayoutOperation
                {
                    Action = LayoutActions.Move, Target = "EndpointRule",
                    LeftTwips = 720, TopTwips = 200
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var line = schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == "EndpointRule");
            line.LeftTwips.Should().Be(720);
            line.TopTwips.Should().Be(200);
            line.WidthTwips.Should().Be(2880);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    // --- Round 2 fix regression tests (F1-F3, T1-T3) ---

    /// <summary>
    /// T1 (the highest-value missing test in the task, per the round-1 review): SyncEndpoints in the
    /// Resize branch had zero coverage and fails SILENTLY, unlike Move. Measured by the reviewer:
    /// deleting `SyncEndpoints(o)` from the Resize case alone keeps every other test in this file
    /// green in memory (the reader reports the requested Width back before save) while persisting the
    /// WRONG width to the .rpt, because RAS actually renders/derives geometry from Right/Bottom, and
    /// an unsynced Right survives save-and-reopen as the old, wider value. This test only passes if
    /// Resize keeps Right/Bottom in sync with Width/Height.
    /// </summary>
    [Fact]
    public void Apply_ResizingALineNarrowerPersistsTheRequestedWidthNotTheStaleEndpoint()
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
                    Action = LayoutActions.AddLine, Section = sectionName, NewName = "ShrinkingRule",
                    LeftTwips = 0, TopTwips = 100, WidthTwips = 2880, HeightTwips = 0
                },
                new LayoutOperation
                {
                    Action = LayoutActions.Resize, Target = "ShrinkingRule",
                    WidthTwips = 1440, HeightTwips = 0
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var line = schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == "ShrinkingRule");
            line.WidthTwips.Should().Be(1440,
                because: "an unsynced Right endpoint would persist the old 2880-wide geometry even though the reader reads Width=1440 back before save");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// T2: ObjectInfo exposes only Left/Top/Width/Height, so the other endpoint tests in this file
    /// verify Right/Bottom only by proxy (through Width/Height after reopen). This test reopens the
    /// saved report and asserts Right/Bottom directly on the RAS object itself -- the one property
    /// C3/SyncEndpoints exists to protect, and the only test that observes it directly.
    /// </summary>
    [Fact]
    public void Apply_MovedAndResizedLineHasConsistentRightAndBottomOnTheRasObjectItself()
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
                    Action = LayoutActions.AddLine, Section = sectionName, NewName = "RasCheckedRule",
                    LeftTwips = 0, TopTwips = 100, WidthTwips = 2880, HeightTwips = 0
                },
                new LayoutOperation
                {
                    Action = LayoutActions.Move, Target = "RasCheckedRule", LeftTwips = 360, TopTwips = 150
                },
                new LayoutOperation
                {
                    Action = LayoutActions.Resize, Target = "RasCheckedRule", WidthTwips = 1000, HeightTwips = 0
                }
            }
        };

        var dest = TempRpt();
        var sourceBefore = File.ReadAllBytes(Fixtures.SampleReport);
        using (var session = CrystalSession.Open(Fixtures.SampleReport))
        {
            LayoutApplier.Apply(session, plan);
            session.SaveAs(dest, overwrite: false);
        }
        File.ReadAllBytes(Fixtures.SampleReport).Should().Equal(sourceBefore,
            because: "the source report must never be modified");

        try
        {
            using var reopened = CrystalSession.Open(dest);
            var all = reopened.Document.ReportDefController.ReportObjectController.GetAllReportObjects();
            ISCRLineObject line = null;
            for (var i = 0; i < all.Count; i++)
            {
                if (all[i] is ISCRLineObject candidate &&
                    string.Equals(candidate.Name, "RasCheckedRule", StringComparison.OrdinalIgnoreCase))
                {
                    line = candidate;
                    break;
                }
            }

            line.Should().NotBeNull();
            line!.Left.Should().Be(360);
            line.Top.Should().Be(150);
            line.Width.Should().Be(1000);
            line.Right.Should().Be(line.Left + line.Width);
            line.Bottom.Should().Be(line.Top + line.Height);
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    /// <summary>
    /// F2: a mid-plan applier failure (not a validation failure) must mark the session faulted so
    /// SaveAs refuses to persist a half-applied document. F1 now rejects a both-axes-non-zero line at
    /// validation time, which means the original repro (a validator-legal diagonal addLine reaching
    /// the applier and throwing a raw COMException) can no longer be driven through the public
    /// LayoutApplier.Apply entry point -- LayoutPlanValidator.Validate would reject it first, and the
    /// document would never be touched at all. To still exercise the applier-level failure path (as
    /// opposed to the already-covered validation-failure path in
    /// Apply_ThrowsInvalidPlanExceptionAndChangesNothingWhenThePlanIsInvalid), this test calls the
    /// internal LayoutApplier.ApplyOperationsWithoutValidation seam directly with the same
    /// good-line-then-diagonal-line plan the round-1 review used to demonstrate the bug, bypassing
    /// LayoutPlanValidator entirely (VibeyReports.CrystalWorker.Tests has InternalsVisibleTo access).
    /// Production code always goes through the public Apply, which validates first.
    /// </summary>
    [Fact]
    public void Apply_WhenAnOperationFailsMidPlan_TheSessionRefusesToSave()
    {
        string sectionName;
        using var session = CrystalSession.Open(Fixtures.SampleReport);
        sectionName = ReportReader.Read(session).Sections.First(x => x.HeightTwips >= 400).Name;

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.AddLine, Section = sectionName,
                    NewName = "GoodRule", LeftTwips = 0, TopTwips = 0, WidthTwips = 2880, HeightTwips = 0 },
                // Both axes non-zero: LayoutPlanValidator (F1) would now reject this, so it is only
                // reachable by calling the internal, validation-free apply path below.
                new LayoutOperation { Action = LayoutActions.AddLine, Section = sectionName,
                    NewName = "BadRule",  LeftTwips = 0, TopTwips = 10, WidthTwips = 2880, HeightTwips = 340 }
            }
        };

        Action apply = () => LayoutApplier.ApplyOperationsWithoutValidation(session, plan);
        apply.Should().Throw<Exception>();

        session.IsFaulted.Should().BeTrue();

        var dest = TempRpt();
        Action save = () => session.SaveAs(dest, overwrite: false);
        save.Should().Throw<InvalidOperationException>().WithMessage("*partially-applied*");
        File.Exists(dest).Should().BeFalse();
    }

    // --- Task 6b: addField ---

    /// <summary>
    /// SampleReport.rpt's only Details section (DetailSection1) is 221 twips tall -- there is no
    /// Details section on it at least 260 twips tall, so this test uses PMSV10_IndPerfOverview.rpt
    /// instead, whose DetailSection1 is 13104 twips tall and whose AvailableFields is populated
    /// (measured directly; see task-6b-report.md).
    /// </summary>
    [Fact]
    public void Apply_AddsABoundFieldWhoseDataSourceSurvivesSaveAndReopen()
    {
        var sourcePath = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");
        string sectionName;
        string fieldRef;
        using (var s = CrystalSession.Open(sourcePath))
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

        var schemaAfter = ApplyAndReread(plan, out var saved, sourcePath);
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
}
