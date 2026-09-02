using System;
using System.Collections.Generic;
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
    /// and every test in this file that goes through this helper mutates a session opened directly
    /// on one of the fixtures under tests/fixtures (Fixtures.SampleReport by default, or another
    /// fixture passed via sourcePath), so checking it here covers all of them for free. Delegates
    /// to <see cref="ApplyAndRereadWithResult"/> so the never-touch-the-source assertion lives in
    /// exactly one place and cannot drift between the two helpers.
    /// </summary>
    private static ReportSchema ApplyAndReread(LayoutPlan plan, out string savedPath, string sourcePath = null)
        => ApplyAndRereadWithResult(plan, out _, out savedPath, sourcePath);

    /// <summary>
    /// Same shape as <see cref="ApplyAndReread"/> (including the never-touch-the-source assertion)
    /// but also hands back the <see cref="LayoutApplier.ApplyResult"/> itself, needed by the
    /// removeObject tests to assert on RemovedObjects rather than only the resulting schema.
    /// </summary>
    private static ReportSchema ApplyAndRereadWithResult(
        LayoutPlan plan, out LayoutApplier.ApplyResult result, out string savedPath, string sourcePath = null)
    {
        sourcePath = sourcePath ?? Fixtures.SampleReport;
        var sourceBefore = File.ReadAllBytes(sourcePath);

        var dest = TempRpt();
        using (var session = CrystalSession.Open(sourcePath))
        {
            result = LayoutApplier.Apply(session, plan);
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
    /// Up to <paramref name="count"/> distinct Text/Field object names from SampleReport.rpt, for
    /// tests that need several independent targets in one plan (e.g. a mixed removeObject plan).
    /// </summary>
    private static List<string> DistinctTextOrFieldNames(int count)
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);
        var schema = ReportReader.Read(session);
        return schema.Sections
            .SelectMany(s => s.Objects)
            .Where(o => o.Kind == "Text" || o.Kind == "Field")
            .Select(o => o.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();
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

    /// <summary>
    /// Final review F3: LayoutPlanValidator.FontableKinds omitted "FieldHeading" even though
    /// FieldHeadingObjectClass implements ISCRTextObject and genuinely carries a font --
    /// SampleReport.rpt measurably has two such objects (Text1, Text2; confirmed by a temporary
    /// diagnostic walking ReportReader.Read's Kind values before writing this test), so this is a
    /// real end-to-end regression test, not one that can only pass by coincidence.
    /// </summary>
    [Fact]
    public void Apply_SetsBoldOnAFieldHeadingObjectAndItSurvivesSaveAndReopen()
    {
        string name;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
            name = ReportReader.Read(s).Sections.SelectMany(x => x.Objects).First(o => o.Kind == "FieldHeading").Name;

        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.SetBold, Target = name, Bold = true } }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var styled = schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == name);
            styled.Kind.Should().Be("FieldHeading");
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

        LayoutApplier.Apply(session, plan).OperationsApplied.Should().Be(2);
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
                    // Round-1 fix (T4): non-zero Left/Top, asserted below -- at 0,0 the position
                    // assertion could not distinguish "positioned as requested" from "RAS defaulted".
                    LeftTwips = 200, TopTwips = 40, WidthTwips = 2500, HeightTwips = 240
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
            added.LeftTwips.Should().Be(200);
            added.TopTwips.Should().Be(40);
            added.WidthTwips.Should().Be(2500);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// Round-1 fix (T1), the priority test: the security boundary is otherwise only proven at the
    /// validator level, against hand-rolled schemas. Nothing previously asserted that
    /// LayoutApplier.Apply itself rejects a bogus fieldRef against a real, live report and leaves
    /// the document unmutated -- a refactor that reordered Apply to mutate before validating, or
    /// that passed a stale schema to Validate, would have kept all 40 Contracts tests green.
    /// </summary>
    [Fact]
    public void Apply_RejectsAFieldRefThatIsNotInTheReportsDataSource()
    {
        using var session = CrystalSession.Open(Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt"));
        var before = ReportReader.Read(session);
        var sectionName = before.Sections.First(s => s.Kind == "Details").Name;
        var countBefore = before.Sections.SelectMany(s => s.Objects).Count();

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddField, Section = sectionName, NewName = "fBogus",
                    FieldRef = "{Command.salary_secret_not_in_this_report}",
                    LeftTwips = 100, TopTwips = 20, WidthTwips = 2000, HeightTwips = 240
                }
            }
        };

        Action act = () => LayoutApplier.Apply(session, plan);

        act.Should().Throw<LayoutApplier.InvalidPlanException>();
        session.IsFaulted.Should().BeFalse(because: "validation runs before any mutation");
        ReportReader.Read(session).Sections.SelectMany(s => s.Objects).Count().Should().Be(countBefore);
    }

    /// <summary>
    /// Round-1 fix (T3): the entire deviation from the brief was about resolving each field's real
    /// CrFieldValueTypeEnum before binding it (see AddField's F1 fix), yet the persistence test above
    /// only ever exercised AvailableFields.First(). This walks every distinct ValueType the fixture's
    /// AvailableFields reports (String/Number/Date/Currency/Boolean/... whichever this fixture has),
    /// adds one field of each, and asserts every DataSource round-trips. The value type itself is not
    /// asserted -- ObjectInfo does not expose it -- proving Add does not throw and the binding
    /// persists across several distinct types is the valuable part.
    /// </summary>
    [Fact]
    public void Apply_AddsFieldsOfMultipleDistinctValueTypesAndEachDataSourceRoundTrips()
    {
        var sourcePath = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");
        string sectionName;
        List<FieldInfo> distinctByType;
        using (var s = CrystalSession.Open(sourcePath))
        {
            var schema = ReportReader.Read(s);
            sectionName = schema.Sections.First(x => x.Kind == "Details" && x.HeightTwips >= 2000).Name;
            distinctByType = schema.AvailableFields
                .GroupBy(f => f.ValueType)
                .Select(g => g.First())
                .ToList();
        }

        distinctByType.Count.Should().BeGreaterThanOrEqualTo(2,
            because: "the fixture must expose more than one field ValueType for this test to be meaningful");

        var plan = new LayoutPlan { Operations = new List<LayoutOperation>() };
        var top = 0;
        foreach (var (field, i) in distinctByType.Select((f, i) => (f, i)))
        {
            plan.Operations.Add(new LayoutOperation
            {
                Action = LayoutActions.AddField, Section = sectionName, NewName = $"VibeyTypeField{i}",
                FieldRef = field.FormulaForm,
                LeftTwips = 0, TopTwips = top, WidthTwips = 2500, HeightTwips = 240
            });
            top += 260;
        }

        var schemaAfter = ApplyAndReread(plan, out var saved, sourcePath);
        try
        {
            var objects = schemaAfter.Sections.SelectMany(s => s.Objects).ToList();
            for (var i = 0; i < distinctByType.Count; i++)
            {
                var added = objects.SingleOrDefault(o => o.Name == $"VibeyTypeField{i}");
                added.Should().NotBeNull(because: $"field of ValueType \"{distinctByType[i].ValueType}\" should have been added");
                added!.DataSource.Should().Be(distinctByType[i].FormulaForm);
            }
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// Round-1 fix (T4): the validator test proves a Kind="Field" sim-object is fontable, but
    /// nothing previously proved FindObject can locate a newly-added field by newName on a real
    /// document, or that WithFont's ISCRFieldObject arm works on a field addField itself created
    /// (as opposed to one already present on the fixture).
    /// </summary>
    [Fact]
    public void Apply_AddsAFieldThenSetsBoldInOnePlanAndBothSurviveSaveAndReopen()
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
                    Action = LayoutActions.AddField, Section = sectionName, NewName = "VibeyBoldField",
                    FieldRef = fieldRef,
                    LeftTwips = 100, TopTwips = 60, WidthTwips = 2500, HeightTwips = 240
                },
                new LayoutOperation { Action = LayoutActions.SetBold, Target = "VibeyBoldField", Bold = true }
            }
        };

        var schemaAfter = ApplyAndReread(plan, out var saved, sourcePath);
        try
        {
            var added = schemaAfter.Sections.SelectMany(s => s.Objects)
                                   .SingleOrDefault(o => o.Name == "VibeyBoldField");
            added.Should().NotBeNull();
            added!.Kind.Should().Be("Field");
            added.Bold.Should().BeTrue();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    // --- Task 6c: addText font defaulting ---

    /// <summary>
    /// Coverage that addText immediately followed by setBold on the same new object -- the
    /// project's primary stated use case, four bold column headings over a details band -- works
    /// end to end and survives save/reopen. NOT the regression test for a null-FontColor crash:
    /// unlike AddField (whose FieldObjectClass really does come back from Add() with
    /// FontColor == null, confirmed by reverting its fix and rerunning), a freshly constructed
    /// TextObjectClass's outer FontColor is never null -- RAS supplies its own default
    /// ("MS Shell Dlg"), so WithFont's null-FontColor guard is never hit here and this test passes
    /// even against the pre-fix code (verified directly). Pre-fix, the actual defect on this path
    /// was a silently wrong font, not a throw; see
    /// Apply_AddedTextInheritsTheFontOfExistingObjectsInItsSection for the test that genuinely
    /// fails before the fix, and task-6c-report.md for the full investigation.
    /// </summary>
    [Fact]
    public void Apply_AddsTextThenSetsBoldInOnePlanAndBothSurviveSaveAndReopen()
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
                    Action = LayoutActions.AddText, Section = sectionName, NewName = "VibeyBoldText",
                    Text = "Employee Name", LeftTwips = 0, TopTwips = 0, WidthTwips = 2500, HeightTwips = 240
                },
                new LayoutOperation { Action = LayoutActions.SetBold, Target = "VibeyBoldText", Bold = true }
            }
        };

        var schemaAfter = ApplyAndReread(plan, out var saved);
        try
        {
            var added = schemaAfter.Sections.SelectMany(s => s.Objects)
                                   .SingleOrDefault(o => o.Name == "VibeyBoldText");
            added.Should().NotBeNull();
            added!.Kind.Should().Be("Text");
            added.Text.Should().Contain("Employee Name");
            added.Bold.Should().BeTrue();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// Coverage that setFont and setFontSize also work on a newly added text object, not just
    /// setBold -- exercised together in one plan so a regression in either shows up here. As with
    /// Apply_AddsTextThenSetsBoldInOnePlanAndBothSurviveSaveAndReopen above, this is not a
    /// null-FontColor regression test: a freshly constructed TextObjectClass's outer FontColor is
    /// never null (RAS defaults it to "MS Shell Dlg"), so this test passes even against the pre-fix
    /// code (verified directly). See Apply_AddedTextInheritsTheFontOfExistingObjectsInItsSection
    /// for the test that genuinely fails before the fix.
    /// </summary>
    [Fact]
    public void Apply_AddsTextThenSetsFontAndFontSizeInOnePlanAndBothSurviveSaveAndReopen()
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
                    Action = LayoutActions.AddText, Section = sectionName, NewName = "VibeyStyledText",
                    Text = "Styled", LeftTwips = 0, TopTwips = 0, WidthTwips = 2500, HeightTwips = 240
                },
                new LayoutOperation { Action = LayoutActions.SetFont, Target = "VibeyStyledText", FontName = "Calibri" },
                new LayoutOperation { Action = LayoutActions.SetFontSize, Target = "VibeyStyledText", FontSizePt = 14f }
            }
        };

        var schemaAfter = ApplyAndReread(plan, out var saved);
        try
        {
            var added = schemaAfter.Sections.SelectMany(s => s.Objects)
                                   .SingleOrDefault(o => o.Name == "VibeyStyledText");
            added.Should().NotBeNull();
            added!.FontName.Should().Be("Calibri");
            added.FontSizePt.Should().Be(14f);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// A hardcoded Arial default would silently mismatch a report whose other objects use a
    /// different font, forcing the caller to issue a setFont every time just to compensate.
    /// PMSV10_IndPerfOverview.rpt's PageHeaderSection1 already carries three Text objects
    /// (Text27/28/29), all measured directly as Segoe UI, not Arial -- the one fixture/section
    /// combination on hand that can actually distinguish "inherited the section's existing font"
    /// from "defaulted to Arial" (every other fixture's candidate sections are Arial already,
    /// which would let a hardcoded-Arial default pass this assertion by coincidence).
    /// </summary>
    [Fact]
    public void Apply_AddedTextInheritsTheFontOfExistingObjectsInItsSection()
    {
        var sourcePath = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");
        var sectionName = "PageHeaderSection1";

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddText, Section = sectionName, NewName = "VibeyInheritedFontText",
                    Text = "Inherited", LeftTwips = 0, TopTwips = 1000, WidthTwips = 2000, HeightTwips = 200
                }
            }
        };

        var schemaAfter = ApplyAndReread(plan, out var saved, sourcePath);
        try
        {
            var added = schemaAfter.Sections.SelectMany(s => s.Objects)
                                   .SingleOrDefault(o => o.Name == "VibeyInheritedFontText");
            added.Should().NotBeNull();
            added!.FontName.Should().Be("Segoe UI",
                because: "it should inherit the font already used by PageHeaderSection1's existing " +
                         "Text objects rather than default to Arial");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    // --- removeObject ---

    /// <summary>
    /// Removes a real object from a fixture, saves, reopens, and confirms it is gone from the
    /// returned schema while every other object survives -- the core removeObject contract, and
    /// the reason ApplyAndRereadWithResult still runs the source-byte-identical check every other
    /// test in this file relies on.
    /// </summary>
    [Fact]
    public void Apply_RemovesAnObjectAndItIsAbsentAfterReopenWhileOthersSurvive()
    {
        List<string> namesBefore;
        string toRemove;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
        {
            var before = ReportReader.Read(s);
            namesBefore = before.Sections.SelectMany(x => x.Objects).Select(o => o.Name).ToList();
            toRemove = before.Sections.SelectMany(x => x.Objects).First(o => o.Kind == "Text" || o.Kind == "Field").Name;
        }

        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.RemoveObject, Target = toRemove } }
        };

        var schema = ApplyAndRereadWithResult(plan, out _, out var saved);
        try
        {
            var namesAfter = schema.Sections.SelectMany(s => s.Objects).Select(o => o.Name).ToList();
            namesAfter.Should().NotContain(toRemove);
            namesAfter.Should().BeEquivalentTo(namesBefore.Where(n => n != toRemove),
                because: "every object other than the removed one must survive untouched");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// The reporting half of the task: ApplyResult.RemovedObjects must name what was destroyed so
    /// an AI agent (and the user) can see it, since OperationsApplied alone cannot distinguish a
    /// removal from any other kind of operation.
    /// </summary>
    [Fact]
    public void Apply_ReportsTheRemovedObjectNameInApplyResult()
    {
        var toRemove = FirstTextOrFieldName();
        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.RemoveObject, Target = toRemove } }
        };

        ApplyAndRereadWithResult(plan, out var result, out var saved);
        try
        {
            result.OperationsApplied.Should().Be(1);
            result.RemovedObjects.Should().ContainSingle().Which.Should().Be(toRemove);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// Findings F1: every other removeObject test uses a single-operation plan, so a regression
    /// that reports every operation's target as removed (not just removeObject's) would still
    /// pass them all -- e.g. moving `result.RemovedObjects.Add(op.Target!)` out of the switch's
    /// RemoveObject case and below the whole switch. A plan with a non-removal operation plus two
    /// removals catches that: OperationsApplied must count all three, and RemovedObjects must be
    /// exactly the two removed names, in order -- not the bold target, not duplicated, not just
    /// the last one.
    /// </summary>
    [Fact]
    public void Apply_ReportsExactlyTheRemovedObjectsFromAMixedPlanInOrder()
    {
        var names = DistinctTextOrFieldNames(3);
        names.Should().HaveCountGreaterThanOrEqualTo(3,
            because: "this test needs three distinct Text/Field objects on SampleReport.rpt: one to " +
                     "restyle and leave in place, two to remove");
        var keep = names[0];
        var removeFirst = names[1];
        var removeSecond = names[2];

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.SetBold, Target = keep, Bold = true },
                new LayoutOperation { Action = LayoutActions.RemoveObject, Target = removeFirst },
                new LayoutOperation { Action = LayoutActions.RemoveObject, Target = removeSecond }
            }
        };

        var schema = ApplyAndRereadWithResult(plan, out var result, out var saved);
        try
        {
            result.OperationsApplied.Should().Be(3);
            result.RemovedObjects.Should().Equal(
                new List<string> { removeFirst, removeSecond },
                because: "RemovedObjects must name exactly the two removed objects, in order -- " +
                         "not the setBold target, which was only restyled");

            var namesAfter = schema.Sections.SelectMany(s => s.Objects).Select(o => o.Name).ToList();
            namesAfter.Should().Contain(keep, because: "the setBold target was never removed");
            namesAfter.Should().NotContain(removeFirst);
            namesAfter.Should().NotContain(removeSecond);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// Findings F5: FindObject matches OrdinalIgnoreCase, so a plan targeting a different casing
    /// than the report's own object name must still report the report's canonical Name in
    /// RemovedObjects, not the caller-supplied casing (AddField already does this for the same
    /// reason).
    /// </summary>
    [Fact]
    public void Apply_ReportsRemovedObjectUsingTheReportsCanonicalNameNotTheCallersCasing()
    {
        var toRemove = FirstTextOrFieldName();
        var differentCasing = toRemove.ToUpperInvariant();
        differentCasing.Should().NotBe(toRemove,
            because: "the fixture's object name must contain a lowercase letter for this test to " +
                     "actually exercise a casing mismatch");

        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.RemoveObject, Target = differentCasing } }
        };

        ApplyAndRereadWithResult(plan, out var result, out var saved);
        try
        {
            result.RemovedObjects.Should().ContainSingle().Which.Should().Be(toRemove,
                because: "RemovedObjects must echo the report's canonical name, not the caller's casing");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// Findings F2: the apply_layout description promises removeObject works on Field/Text plus
    /// Subreport/Chart/Crosstab, but every other test here selects a Text or Field target. This
    /// proves a third kind (Line) is genuinely accepted by RAS, and simultaneously exercises the
    /// previously-untested "remove an object added earlier in the same plan" ordering case.
    /// </summary>
    [Fact]
    public void Apply_RemovesALineAddedEarlierInTheSamePlan()
    {
        string sectionName;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
            sectionName = ReportReader.Read(s).Sections.First(x => x.HeightTwips >= 400).Name;

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.AddLine, Section = sectionName, NewName = "VibeyRemoveMeLine",
                    LeftTwips = 0, TopTwips = 0, WidthTwips = 2880, HeightTwips = 0 },
                new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "VibeyRemoveMeLine" }
            }
        };

        var schema = ApplyAndRereadWithResult(plan, out var result, out var saved);
        try
        {
            result.OperationsApplied.Should().Be(2);
            result.RemovedObjects.Should().ContainSingle().Which.Should().Be("VibeyRemoveMeLine");
            schema.Sections.SelectMany(s => s.Objects).Should().NotContain(o => o.Name == "VibeyRemoveMeLine");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    // --- colour operations ---

    /// <summary>
    /// setTextColor writes FontColor.Color; ReportReader.ToHex reads it back through the same
    /// ColorRef helper, so this is the round-trip that proves the write and read halves agree
    /// with each other (independent of whether the COLORREF channel-order hypothesis itself is
    /// right -- that is what the swatch report settles).
    /// </summary>
    [Fact]
    public void Apply_SetsTextColorAndItSurvivesSaveAndReopen()
    {
        var name = FirstTextOrFieldName();
        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.SetTextColor, Target = name, Color = "#1F2A37" } }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == name)
                  .TextColorHex.Should().Be("#1F2A37");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_SetsSectionBackgroundAndItSurvivesSaveAndReopen()
    {
        string sectionName;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
            sectionName = ReportReader.Read(s).Sections.First().Name;

        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.SetSectionBackground, Section = sectionName, Color = "#1F2A37" } }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            schema.Sections.Single(s => s.Name == sectionName).BackgroundColorHex.Should().Be("#1F2A37");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_SetsBoxFillAndLineColorAndBothSurviveSaveAndReopen()
    {
        string sectionName;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
            sectionName = ReportReader.Read(s).Sections.First(x => x.HeightTwips >= 400).Name;

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.AddBox, Section = sectionName, NewName = "VibeyColourBox",
                    LeftTwips = 0, TopTwips = 0, WidthTwips = 2880, HeightTwips = 340 },
                new LayoutOperation { Action = LayoutActions.SetFillColor, Target = "VibeyColourBox", Color = "#00AA00" },
                new LayoutOperation { Action = LayoutActions.SetLineColor, Target = "VibeyColourBox", Color = "#0000FF" }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var box = schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == "VibeyColourBox");
            box.FillColorHex.Should().Be("#00AA00");
            box.LineColorHex.Should().Be("#0000FF");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_SetsLineColorOnALine()
    {
        string sectionName;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
            sectionName = ReportReader.Read(s).Sections.First(x => x.HeightTwips >= 400).Name;

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.AddLine, Section = sectionName, NewName = "VibeyColourRule",
                    LeftTwips = 0, TopTwips = 0, WidthTwips = 2880, HeightTwips = 0 },
                new LayoutOperation { Action = LayoutActions.SetLineColor, Target = "VibeyColourRule", Color = "#FF0000" }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == "VibeyColourRule")
                  .LineColorHex.Should().Be("#FF0000");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    // --- addSubreport / setSubreportLink ---

    /// <summary>
    /// Imports tests/fixtures/SampleReport.rpt as a sub-report into PMSV10_IndPerfOverview.rpt and
    /// asserts an object of kind Subreport exists at the requested geometry after save/reopen.
    /// Measured (see task report): ImportSubreportEx's "Name" argument becomes
    /// ISCRSubreportObject.SubreportName, NOT the placed report OBJECT's own Name -- Crystal
    /// auto-numbers the container object itself ("Subreport3" here, since the fixture already has
    /// two). So this locates the added object by kind+geometry rather than by NewName, which would
    /// never match.
    /// </summary>
    [Fact]
    public void Apply_AddsASubreportAndItExistsAtTheRequestedGeometryAfterSaveAndReopen()
    {
        var sourcePath = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");
        string sectionName;
        using (var s = CrystalSession.Open(sourcePath))
        {
            var schema = ReportReader.Read(s);
            sectionName = schema.Sections.First(x => x.Kind == "Details" && x.HeightTwips >= 400).Name;
        }

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddSubreport, Section = sectionName, NewName = "VibeyGoalDetail",
                    ReportPath = Fixtures.SampleReport,
                    LeftTwips = 100, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
                }
            }
        };

        var schemaAfter = ApplyAndReread(plan, out var saved, sourcePath);
        try
        {
            var added = schemaAfter.Sections.SelectMany(s => s.Objects)
                .SingleOrDefault(o => o.Kind == "Subreport" && o.LeftTwips == 100 && o.TopTwips == 20
                                       && o.WidthTwips == 3000 && o.HeightTwips == 300);
            added.Should().NotBeNull();

            // F6: asserting added.Kind == "Subreport" here could never fail -- the LINQ predicate
            // above already filters on exactly that. Assert the two things the predicate does NOT
            // establish instead. subreportName is the identifier setSubreportLink is keyed by, and
            // is a DIFFERENT string from the placed object's own auto-numbered Name; reporting it
            // is what makes an already-embedded sub-report linkable at all.
            added!.SubreportName.Should().Be("VibeyGoalDetail");
            added.Name.Should().NotBe("VibeyGoalDetail");

            // Also covers ReportReader.ReadSubreportLinks's otherwise-untested bare catch: a
            // sub-report with no links yet must read back as an empty list, never as null and
            // never by failing the whole report read.
            added.SubreportLinks.Should().NotBeNull().And.BeEmpty();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// F2, the finding the whole review turns on: linking a sub-report that is ALREADY embedded,
    /// rather than one added by this same plan. Before the fix ObjectInfo carried no SubreportName,
    /// so the only name a caller could obtain from read_report was the placed object's
    /// ("Subreport1"), and setSubreportLink against that fails at the COM boundary with the
    /// unhelpful "This value is write-only."; the sub-report's real name was simply not reachable.
    ///
    /// Measured directly against out/reports/PMSV10_GoalAlignCascade.subreport.rpt through the real
    /// worker process before this test was written: the read reports name="Subreport1",
    /// subreportName="GoalDetail"; a plan whose only operation is a setSubreportLink targeting
    /// "GoalDetail" applies ok:true; and the saved report reads back exactly one link. A second
    /// apply against THAT output appends a second link and keeps the first, which is also what
    /// proves F3's narrowed COMException tolerance is not silently replacing the collection.
    /// </summary>
    [Fact]
    public void Apply_LinksASubreportThatWasAlreadyEmbeddedByAnEarlierPlan()
    {
        const string subLinkField = "{Command.CardCode}";
        var sourcePath = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");
        string sectionName;
        string mainLinkField;
        using (var s = CrystalSession.Open(sourcePath))
        {
            var schema = ReportReader.Read(s);
            sectionName = schema.Sections.First(x => x.Kind == "Details" && x.HeightTwips >= 400).Name;
            mainLinkField = schema.AvailableFields.First(f => f.FormulaForm.Contains("emp_display_number")).FormulaForm;
        }

        // Plan 1: embed the sub-report and nothing else. This stands in for "an earlier
        // apply_layout call", the case the previous implementation declared unsupported.
        var embedPlan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddSubreport, Section = sectionName, NewName = "VibeyGoalDetail",
                    ReportPath = Fixtures.SampleReport,
                    LeftTwips = 100, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
                }
            }
        };

        var embedded = ApplyAndReread(embedPlan, out var embeddedPath, sourcePath);
        try
        {
            // The identity split, read back off a saved file rather than assumed.
            // Selected by SubreportName, NOT by Kind alone: PMSV10_IndPerfOverview.rpt already
            // embeds two sub-reports of its own ("company logo" in the page header and "stage wise
            // eval" in the details band), so a bare Single(o => o.Kind == "Subreport") matched
            // three objects and threw. Picking the one this plan added is also the stronger
            // assertion -- it proves the newly embedded sub-report is addressable by the newName
            // the plan chose, which is the whole point of the test, rather than assuming there is
            // only one sub-report to find.
            var subreport = embedded.Sections.SelectMany(s => s.Objects)
                .Single(o => o.Kind == "Subreport" && o.SubreportName == "VibeyGoalDetail");
            subreport.SubreportName.Should().Be("VibeyGoalDetail");
            subreport.Name.Should().NotBe("VibeyGoalDetail");

            // Plan 2 targets the SAVED report by that subreportName -- a name no operation in this
            // plan created. This is the whole point of reporting SubreportName.
            var linkPlan = new LayoutPlan
            {
                Operations =
                {
                    new LayoutOperation
                    {
                        Action = LayoutActions.SetSubreportLink,
                        Target = subreport.SubreportName,
                        MainReportField = mainLinkField,
                        SubreportField = subLinkField
                        // LinkedParameter deliberately omitted: Crystal discards it and substitutes
                        // "{?Pm-<mainReportField>}", so the validator no longer requires it.
                    }
                }
            };

            var linked = ApplyAndReread(linkPlan, out var linkedPath, embeddedPath);
            try
            {
                var links = linked.Sections.SelectMany(s => s.Objects)
                                  .Single(o => o.SubreportName == "VibeyGoalDetail").SubreportLinks;
                links.Should().NotBeNull();
                links!.Should().ContainSingle();
                links[0].MainReportFieldName.Should().Be(mainLinkField);
                links[0].SubreportFieldName.Should().Be(subLinkField);
                // Measured, not assumed: Crystal substitutes its own parameter for whatever is
                // written (or, as here, for nothing at all), so assert the substitution rather than
                // a literal the SDK does not guarantee to preserve.
                links[0].LinkedParameterName.Should().Contain("?Pm-");

                // F3, the destructive case that only shows up ACROSS applies. SetSubreportLinks
                // replaces the whole collection, and the old code caught any COMException from
                // GetSubreportLinks by starting from a fresh one -- which would silently turn this
                // third plan's append into a replace, discarding the link the second plan wrote,
                // and still report ok:true. The narrowed tolerance re-reads and requires exactly
                // one link whenever the fallback is taken, so a real replace now fails loudly
                // instead. Both links must be present, in order, after a completely separate apply
                // against an already-linked file.
                var secondMainField = mainLinkField;
                using (var s2 = CrystalSession.Open(linkedPath))
                {
                    secondMainField = ReportReader.Read(s2).AvailableFields
                        .First(f => f.FormulaForm.Contains("employee_name")).FormulaForm;
                }

                var appendPlan = new LayoutPlan
                {
                    Operations =
                    {
                        new LayoutOperation
                        {
                            Action = LayoutActions.SetSubreportLink,
                            Target = subreport.SubreportName,
                            MainReportField = secondMainField,
                            SubreportField = "{Command.CardName}"
                        }
                    }
                };

                var appended = ApplyAndReread(appendPlan, out var appendedPath, linkedPath);
                try
                {
                    var both = appended.Sections.SelectMany(s => s.Objects)
                                       .Single(o => o.SubreportName == "VibeyGoalDetail").SubreportLinks;
                    both.Should().NotBeNull();
                    both!.Should().HaveCount(2, because: "the earlier plan's link must not be discarded");
                    both[0].MainReportFieldName.Should().Be(mainLinkField);
                    both[0].SubreportFieldName.Should().Be(subLinkField);
                    both[1].MainReportFieldName.Should().Be(secondMainField);
                    both[1].SubreportFieldName.Should().Be("{Command.CardName}");
                }
                finally { if (File.Exists(appendedPath)) File.Delete(appendedPath); }
            }
            finally { if (File.Exists(linkedPath)) File.Delete(linkedPath); }
        }
        finally { if (File.Exists(embeddedPath)) File.Delete(embeddedPath); }
    }

    /// <summary>
    /// F3's applier-side guard, driven through the internal validation-free seam because
    /// LayoutPlanValidator now rejects this shape first (it resolves setSubreportLink's target
    /// against the schema's subreportName values). Measured: without the guard, an unknown
    /// sub-report name reaches SetSubreportLinks and fails with COM "This value is write-only." --
    /// true, but telling the caller nothing about what actually went wrong. SubreportController
    /// .GetSubreportNames() -- present on the SDK surface but unused until now -- is the authority
    /// on which names are addressable, so resolve against it first and say what does exist.
    /// </summary>
    [Fact]
    public void Apply_SetSubreportLinkAgainstAnUnknownSubreportNameSaysSoInsteadOfFailingAtCom()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.SetSubreportLink, Target = "NoSuchSubreport",
                    MainReportField = "{Command.CardCode}", SubreportField = "{Command.CardName}"
                }
            }
        };

        Action apply = () => LayoutApplier.ApplyOperationsWithoutValidation(session, plan);

        apply.Should().Throw<Exception>()
             .WithMessage("*NoSuchSubreport*is not a sub-report in this report*");
    }

    /// <summary>
    /// The priority test for setSubreportLink: SubreportController.SetSubreportLinks replaces the
    /// ENTIRE link collection for the named sub-report, so a naive "build one link and set it"
    /// implementation would let the second setSubreportLink call silently discard the first -- a
    /// report needing both an evaluation-cycle link and an employee-number link would then only
    /// ever apply the last one, discarding real HR data. Two independent setSubreportLink
    /// operations must both survive save/reopen, in order.
    ///
    /// Round-trips MainReportFieldName/SubreportFieldName exactly. Measured directly against the
    /// installed 11.5 RAS (three separate probes, not assumed): LinkedParameterName does NOT
    /// reliably round-trip as given when the target sub-report has no pre-existing parameter by
    /// that name.
    ///   1. LinkedParameterName as a bare string (e.g. "@performance_cycle_id") -- SetSubreportLinks
    ///      itself did not throw, but the SUBSEQUENT SaveAs threw COMException "Invalid value type."
    ///   2. LinkedParameterName in Crystal's own formula form (e.g. "{?performance_cycle_id}") with
    ///      matching MainReportFieldName/SubreportFieldName value types -- SetSubreportLinks
    ///      succeeded and SaveAs succeeded, but on reopen LinkedParameterName came back as
    ///      Crystal's own auto-generated "{?Pm-&lt;mainReportField&gt;}", not the string given.
    ///   3. Same as (2), but with a matching CrystalDecisions.ReportAppServer.DataDefModel.
    ///      ParameterFieldClass pre-registered on the sub-report via
    ///      DataDefController.ParameterFieldController.Add() before calling SetSubreportLinks --
    ///      same "Pm-" substitution still happened, so a minimally-constructed ParameterField is
    ///      not sufficient to make Crystal treat it as the "already exists" case.
    /// SampleReport.rpt (this fixture's sub-report) defines no parameters of its own, so no fixture
    /// available to this test avoids the substitution. This asserts the two LinkedParameterName
    /// values actually persisted are distinct (proving both links are genuinely present, not one
    /// clobbering the other) rather than an exact literal string Crystal itself does not guarantee
    /// to preserve. Whether the substituted parameter still feeds a real stored-procedure input
    /// parameter on a genuinely SP-parameterised sub-report (the customer's actual case) is
    /// unverified -- see the task report.
    /// </summary>
    [Fact]
    public void Apply_AddsTwoSubreportLinksAndBothSurviveSaveAndReopenInOrder()
    {
        var sourcePath = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");
        string sectionName;
        string mainField1;
        string mainField2;
        using (var s = CrystalSession.Open(sourcePath))
        {
            var schema = ReportReader.Read(s);
            sectionName = schema.Sections.First(x => x.Kind == "Details" && x.HeightTwips >= 400).Name;
            mainField1 = schema.AvailableFields.First(f => f.FormulaForm.Contains("emp_display_number")).FormulaForm;
            mainField2 = schema.AvailableFields.First(f => f.FormulaForm.Contains("employee_name")).FormulaForm;
        }

        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddSubreport, Section = sectionName, NewName = "VibeyLinkedSubreport",
                    ReportPath = Fixtures.SampleReport,
                    LeftTwips = 150, TopTwips = 25, WidthTwips = 3000, HeightTwips = 300
                },
                new LayoutOperation
                {
                    // setSubreportLink targeting a sub-report added earlier in the same plan --
                    // the normal usage: SetSubreportLinks/GetSubreportLinks are keyed by the
                    // sub-report's own name (VibeyLinkedSubreport, exactly the newName above), not
                    // by the container object's Crystal-assigned name, so "target" here resolves
                    // correctly without ever needing to know that auto-assigned name.
                    Action = LayoutActions.SetSubreportLink, Target = "VibeyLinkedSubreport",
                    MainReportField = mainField1, SubreportField = "{Command.CardCode}",
                    LinkedParameter = "@performance_cycle_id"
                },
                new LayoutOperation
                {
                    Action = LayoutActions.SetSubreportLink, Target = "VibeyLinkedSubreport",
                    MainReportField = mainField2, SubreportField = "{Command.CardName}",
                    LinkedParameter = "@employee_number"
                }
            }
        };

        var schemaAfter = ApplyAndReread(plan, out var saved, sourcePath);
        try
        {
            var added = schemaAfter.Sections.SelectMany(s => s.Objects)
                .Single(o => o.Kind == "Subreport" && o.LeftTwips == 150 && o.TopTwips == 25
                             && o.WidthTwips == 3000 && o.HeightTwips == 300);

            added.SubreportLinks.Should().NotBeNull();
            added.SubreportLinks!.Should().HaveCount(2,
                because: "SetSubreportLinks must append, not replace -- two setSubreportLink " +
                         "calls must leave both links, not just the last one");

            added.SubreportLinks[0].MainReportFieldName.Should().Be(mainField1);
            added.SubreportLinks[0].SubreportFieldName.Should().Be("{Command.CardCode}");

            added.SubreportLinks[1].MainReportFieldName.Should().Be(mainField2);
            added.SubreportLinks[1].SubreportFieldName.Should().Be("{Command.CardName}");

            added.SubreportLinks[0].LinkedParameterName.Should().NotBeNullOrEmpty();
            added.SubreportLinks[1].LinkedParameterName.Should().NotBeNullOrEmpty();
            added.SubreportLinks[0].LinkedParameterName.Should().NotBe(added.SubreportLinks[1].LinkedParameterName,
                because: "two distinct links must not have collapsed into the same parameter binding");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }
    // ---- data-source table operations ------------------------------------
    //
    // Every claim in these tests was measured against the installed 11.5 RAS before it was
    // written, and two of the measurements contradicted what was expected:
    //
    // 1. RemoveTable does not refuse a table that report objects are still bound to -- it
    //    CASCADE-DELETES them, silently. Measured on out/reports/PMSV10_GoalAlignCascade.rpt:
    //    removing "sp_perf_goal_align_cascade;1" took the report from 70 objects to 59,
    //    "sp_perf_goal_align_detail;1" took 59 to 54 (the five DR0..DR4 fields bound to it), and
    //    "sp_perf_company_logo;1" took 54 to 53. None of those deletions was reported, requested
    //    or recoverable. LayoutPlanValidator refusing the removal while a bound object survives is
    //    the ONLY thing that prevents it.
    // 2. A table that participates in a TableLink needs no link removal first: AddTableLink
    //    (cascade -> detail) followed by RemoveTable("...detail;1") succeeded, and TableLinks went
    //    1 -> 0 on its own.
    //
    // Crystal does refuse some removals of its own accord -- measured on
    // tests/fixtures/Documents.rpt and PMSV10_IndPerfOverview.rpt, where formulas/record selection
    // still reference the table ("There are still fields in the report from this table"). That
    // path is covered below. Removing the link first does NOT help there, and a table with no
    // links at all ("CompanyInfo" in Documents.rpt) is refused identically, which is why the
    // applier has no speculative RemoveTableLink step.
    //
    // addTable and setTableLocation have no round-trip test here on purpose: measured, both make
    // Crystal connect to the database (COMException "Logon failed. Unable to connect: incorrect
    // log on parameters." on every fixture, because Crystal persists a connection's user name but
    // never its password). A test that drove them to success would need a fixture whose saved
    // connection logs on unattended, and one that drove them to failure would open a real network
    // connection -- which, with the VPN down, blocks rather than fails (post-merge finding PM1).
    // Their offline guards are tested instead. See docs/sdk-notes.md.

    /// <summary>
    /// The driving case end to end: strip the objects bound to a table, then drop the table, in
    /// ONE plan. SampleReport.rpt's only table is "Command" with two bound Field objects.
    /// </summary>
    [Fact]
    public void Apply_RemovesADataSourceTableOnceThePlanHasRemovedItsBoundObjects()
    {
        string alias;
        List<string> bound;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
        {
            var before = ReportReader.Read(s);
            alias = before.AvailableFields.Select(f => f.TableAlias).Distinct().Single();
            bound = before.Sections.SelectMany(x => x.Objects)
                          .Where(o => o.DataSource != null && o.DataSource.StartsWith("{" + alias + "."))
                          .Select(o => o.Name).ToList();
        }

        bound.Should().NotBeEmpty(because: "the fixture must actually exercise the bound-object rule");

        var plan = new LayoutPlan();
        foreach (var name in bound)
            plan.Operations.Add(new LayoutOperation { Action = LayoutActions.RemoveObject, Target = name });
        plan.Operations.Add(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = alias });

        var schema = ApplyAndRereadWithResult(plan, out var result, out var saved);
        try
        {
            result.RemovedTables.Should().ContainSingle().Which.Should().Be(alias);
            result.RemovedObjects.Should().BeEquivalentTo(bound);
            schema.AvailableFields.Select(f => f.TableAlias).Should().NotContain(alias,
                because: "the table must be gone from the saved report, not just from the in-memory document");
            schema.Sections.SelectMany(x => x.Objects).Select(o => o.Name).Should().NotIntersectWith(bound);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// The rule the whole operation exists for. Crystal itself would accept this and silently
    /// delete the bound fields (see the block comment above), so the refusal has to come from the
    /// validator -- and it must name the offending objects, or the agent cannot fix the plan.
    /// Driven through the public Apply, i.e. the gate production code actually goes through.
    /// </summary>
    [Fact]
    public void Apply_RefusesToRemoveATableWhileAnObjectIsStillBoundToIt()
    {
        string alias;
        string boundName;
        using (var s = CrystalSession.Open(Fixtures.SampleReport))
        {
            var before = ReportReader.Read(s);
            alias = before.AvailableFields.Select(f => f.TableAlias).Distinct().Single();
            boundName = before.Sections.SelectMany(x => x.Objects)
                              .First(o => o.DataSource != null && o.DataSource.StartsWith("{" + alias + ".")).Name;
        }

        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.RemoveTable, Target = alias } }
        };

        using var session = CrystalSession.Open(Fixtures.SampleReport);
        Action apply = () => LayoutApplier.Apply(session, plan);

        apply.Should().Throw<LayoutApplier.InvalidPlanException>()
             .Which.Result.Errors.Should().ContainSingle()
             .Which.Message.Should().Contain(boundName);
    }

    /// <summary>
    /// Crystal's own refusal, wrapped. PMSV10_IndPerfOverview.rpt refuses to give up its only
    /// table even after every Field object bound to it has been removed -- something else in the
    /// report (formula, record selection, group or sort) still refers to it, and that is outside
    /// what this tool can edit. Measured: the COM message is "There are still fields in the report
    /// from this table.  Please clear them before removing the table." Requires no database.
    /// </summary>
    [Fact]
    public void Apply_WrapsCrystalsOwnRefusalToRemoveATableWithContext()
    {
        var sourcePath = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");
        string alias;
        List<string> bound;
        using (var s = CrystalSession.Open(sourcePath))
        {
            var before = ReportReader.Read(s);
            alias = before.AvailableFields.Select(f => f.TableAlias).Distinct().Single();
            bound = before.Sections.SelectMany(x => x.Objects)
                          .Where(o => o.DataSource != null && o.DataSource.StartsWith("{" + alias + "."))
                          .Select(o => o.Name).ToList();
        }

        var plan = new LayoutPlan();
        foreach (var name in bound)
            plan.Operations.Add(new LayoutOperation { Action = LayoutActions.RemoveObject, Target = name });
        plan.Operations.Add(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = alias });

        using var session = CrystalSession.Open(sourcePath);
        Action apply = () => LayoutApplier.Apply(session, plan);

        var thrown = apply.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("removeTable").And.Contain(alias);
        thrown.InnerException.Should().BeOfType<System.Runtime.InteropServices.COMException>(
            because: "the COM message is the part that says WHY Crystal refused, and it must not be discarded");
    }

    /// <summary>
    /// The applier's own existence guard, which is NOT redundant with the validator's: the
    /// validator deliberately skips its table existence check when ReportSchema.AvailableFields is
    /// empty (no database connection -- absence cannot be proven from an empty list), so an alias
    /// that does not exist can and does reach the applier. Driven through the internal
    /// validation-free seam because with this fixture's fields readable the validator would reject
    /// it first.
    /// </summary>
    [Fact]
    public void Apply_RemoveTableNamesTheAliasesThatDoExistWhenTheTargetDoesNot()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);
        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.RemoveTable, Target = "sp_not_here;1" } }
        };

        Action apply = () => LayoutApplier.ApplyOperationsWithoutValidation(session, plan);

        apply.Should().Throw<InvalidOperationException>()
             .WithMessage("*sp_not_here;1*Aliases present*Command*");
    }

    [Fact]
    public void Apply_AddTableNamesTheAliasesThatDoExistWhenTheSourceAliasDoesNot()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddTable, Target = "sp_not_here;1",
                    TableName = "sp_new;1", NewName = "sp_new;1"
                }
            }
        };

        Action apply = () => LayoutApplier.ApplyOperationsWithoutValidation(session, plan);

        // Fails before any COM call, so this test never opens a database connection.
        apply.Should().Throw<InvalidOperationException>()
             .WithMessage("*addTable*sp_not_here;1*Aliases present*Command*");
    }

    [Fact]
    public void Apply_AddTableRefusesAnAliasThatIsAlreadyInTheDataSource()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddTable, Target = "Command",
                    TableName = "sp_new;1", NewName = "Command"
                }
            }
        };

        Action apply = () => LayoutApplier.ApplyOperationsWithoutValidation(session, plan);

        apply.Should().Throw<InvalidOperationException>()
             .WithMessage("*already in this report's data source*");
    }

    [Fact]
    public void Apply_SetTableLocationNamesTheAliasesThatDoExistWhenTheTargetDoesNot()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.SetTableLocation, Target = "sp_not_here;1", TableName = "sp_new;1"
                }
            }
        };

        Action apply = () => LayoutApplier.ApplyOperationsWithoutValidation(session, plan);

        apply.Should().Throw<InvalidOperationException>()
             .WithMessage("*setTableLocation*sp_not_here;1*Aliases present*Command*");
    }

    // ---- formatting operations ---------------------------------------------------------------
    //
    // Measured geometry of SampleReport.rpt these tests rely on: PageFooterSection1 is 663 twips
    // tall and holds only PageNumber1 at top 442, so twips 0-441 of that section are free for a
    // new object. PageNumber1 itself is a special field of type crFieldValueTypeInt32uField --
    // the only NUMERIC field object on the fixture, which is why the number-format test targets
    // it (see that test for why a String field would not do).

    private static ObjectInfo FindObject(ReportSchema schema, string name) =>
        schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == name);

    private static SectionInfo FindSection(ReportSchema schema, string name) =>
        schema.Sections.Single(s => s.Name == name);

    [Fact]
    public void Apply_SetsAPageBreakAndItSurvivesSaveAndReopen()
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.SetSectionBreak, Section = "DetailSection1", NewPageAfter = true
                },
                new LayoutOperation
                {
                    Action = LayoutActions.SetSectionBreak, Section = "ReportFooterSection1", NewPageBefore = true
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var detail = FindSection(schema, "DetailSection1");
            detail.NewPageAfter.Should().BeTrue();
            // The property NOT asked for must be left alone, or "set one flag" would quietly
            // clobber the other.
            detail.NewPageBefore.Should().BeFalse();

            var footer = FindSection(schema, "ReportFooterSection1");
            footer.NewPageBefore.Should().BeTrue();
            footer.NewPageAfter.Should().BeFalse();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// Every supported specialType, placed and read back. This is the operation the brief singled
    /// out as most likely to hit the trap AddField hit ("The field value type is not valid"),
    /// because a freshly constructed FieldObjectClass does not infer its value type. It does not:
    /// SpecialFieldClass supplies FormulaForm AND Type from SpecialType alone, and Add accepts the
    /// result for all seven.
    /// </summary>
    [Theory]
    [InlineData("pageNumber", "PageNumber")]
    [InlineData("pageNOfM", "PageNofM")]
    [InlineData("totalPageCount", "TotalPageCount")]
    [InlineData("printDate", "PrintDate")]
    [InlineData("printTime", "PrintTime")]
    [InlineData("reportTitle", "ReportTitle")]
    [InlineData("recordNumber", "RecordNumber")]
    public void Apply_AddsASpecialFieldOfEveryTypeAndItSurvivesSaveAndReopen(
        string specialType, string expectedDataSource)
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddSpecialField, Section = "PageFooterSection1",
                    NewName = "SpecialUnderTest", SpecialType = specialType,
                    LeftTwips = 120, TopTwips = 60, WidthTwips = 1400, HeightTwips = 221
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var placed = FindObject(schema, "SpecialUnderTest");
            placed.Kind.Should().Be("Field");
            placed.LeftTwips.Should().Be(120);
            placed.TopTwips.Should().Be(60);
            placed.WidthTwips.Should().Be(1400);
            placed.HeightTwips.Should().Be(221);
            // The binding itself, not just that an object exists. A special field's DataSource is
            // the bare UNBRACED form ("PageNumber"), unlike a database field's "{Table.Field}" --
            // measured against SampleReport's own PrintDate1/PageNumber1. Asserting it is what
            // distinguishes "placed the right special field" from "placed some field".
            placed.DataSource.Should().Be(expectedDataSource);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// A special field is a Field object, so the font operations must reach it. If its simulated
    /// kind regressed, a page-number footer could be placed but never sized or emboldened.
    /// </summary>
    [Fact]
    public void Apply_SpecialFieldIsFontableInTheSamePlanThatAddsIt()
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddSpecialField, Section = "PageFooterSection1",
                    NewName = "Pager", SpecialType = "pageNOfM",
                    LeftTwips = 0, TopTwips = 0, WidthTwips = 1400, HeightTwips = 221
                },
                new LayoutOperation { Action = LayoutActions.SetFontSize, Target = "Pager", FontSizePt = 9f },
                new LayoutOperation { Action = LayoutActions.SetBold, Target = "Pager", Bold = true }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var pager = FindObject(schema, "Pager");
            pager.FontSizePt.Should().Be(9f);
            pager.Bold.Should().BeTrue();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// The regression test for the finding that cost this operation two rounds: while
    /// ISCRCommonFieldFormat.EnableSystemDefault is true, Crystal formats the field from the
    /// locale defaults and DISCARDS NDecimalPlaces/ThousandsSeparator on save -- silently, with no
    /// exception and an ok result. Deleting the EnableSystemDefault=false line from
    /// SetNumberFormat makes the decimalPlaces and thousandsSeparator assertions below fail.
    ///
    /// The target is PageNumber1 deliberately. It is the fixture's only numeric field object
    /// (crFieldValueTypeInt32uField); measured on the String fields CardCode1/CardName1, Crystal
    /// keeps the EnableSystemDefault write but still discards the numeric properties, so a test
    /// written against one of those would assert nothing about the format at all.
    ///
    /// All three requested values differ from the fixture's baseline (dp=0, thousands=true,
    /// suppressIfZero=false), so none of them can pass by accident.
    /// </summary>
    [Fact]
    public void Apply_SetsANumberFormatAndAllThreePropertiesSurviveSaveAndReopen()
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.SetNumberFormat, Target = "PageNumber1",
                    DecimalPlaces = 3, ThousandsSeparator = false, SuppressIfZero = true
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var format = FindObject(schema, "PageNumber1").NumberFormat;
            format.Should().NotBeNull();
            format.DecimalPlaces.Should().Be(3);
            format.ThousandsSeparator.Should().BeFalse();
            format.SuppressIfZero.Should().BeTrue();
            // The gate itself, which the operation must turn off for the three above to mean
            // anything -- and which is reported so the caller is not told a decimalPlaces value
            // that does not describe what renders.
            format.SystemDefault.Should().BeFalse();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// A number format is the goal_id fix: "10,311.00" becomes "10311". Distinct from the test
    /// above because it pins the exact combination the brief calls out, and because writing only
    /// two of the three properties must leave the third alone.
    /// </summary>
    [Fact]
    public void Apply_SetNumberFormatLeavesPropertiesTheCallerDidNotSupplyAlone()
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.SetNumberFormat, Target = "PageNumber1",
                    DecimalPlaces = 0, ThousandsSeparator = false
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var format = FindObject(schema, "PageNumber1").NumberFormat;
            format.DecimalPlaces.Should().Be(0);
            format.ThousandsSeparator.Should().BeFalse();
            // Never supplied, and the fixture's value is false: it must not have been written.
            format.SuppressIfZero.Should().BeFalse();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    [Fact]
    public void Apply_SetsCanGrowAndObjectSuppressAndBothSurviveSaveAndReopen()
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.SetCanGrow, Target = "CardName1", CanGrow = true },
                new LayoutOperation { Action = LayoutActions.SetSuppress, Target = "CardCode1", Suppress = true }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var grown = FindObject(schema, "CardName1");
            grown.CanGrow.Should().BeTrue();
            // canGrow and suppressed are separate properties on the same ObjectFormat; setting one
            // must not set the other.
            grown.Suppressed.Should().BeFalse();

            var hidden = FindObject(schema, "CardCode1");
            hidden.Suppressed.Should().BeTrue();
            hidden.CanGrow.Should().BeFalse();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// setSuppress's other host. EnableSuppress is one Crystal property on two different format
    /// types, which is why this is one operation rather than two -- and why both paths need
    /// covering.
    /// </summary>
    [Fact]
    public void Apply_SuppressesASectionWithSuppressIfBlankAndBothSurviveSaveAndReopen()
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.SetSuppress, Section = "ReportHeaderSection1",
                    Suppress = true, SuppressIfBlank = true
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var section = FindSection(schema, "ReportHeaderSection1");
            section.Suppressed.Should().BeTrue();
            section.SuppressIfBlank.Should().BeTrue();
            // A different section is untouched, so this cannot pass by suppressing everything.
            FindSection(schema, "DetailSection1").Suppressed.Should().BeFalse();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// suppressIfBlank is optional on the section form: omitting it must leave the section's
    /// existing value alone rather than defaulting it to false.
    /// </summary>
    [Fact]
    public void Apply_SectionSuppressWithoutSuppressIfBlankLeavesItAlone()
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.SetSuppress, Section = "DetailSection1", Suppress = true
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var section = FindSection(schema, "DetailSection1");
            section.Suppressed.Should().BeTrue();
            section.SuppressIfBlank.Should().BeFalse();
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    // ---- addGroup / addSort ------------------------------------------------

    // SampleReport.rpt's two result fields, both String, neither grouped nor sorted in the fixture.
    private const string CardCode = "{Command.CardCode}";
    private const string CardName = "{Command.CardName}";

    /// <summary>
    /// The load-bearing test of the whole feature: it asserts that the section names
    /// LayoutPlanValidator PREDICTS through GroupSectionNaming are the names Crystal actually
    /// assigns. If that ever diverges, a plan that validates would throw mid-apply -- the exact
    /// failure mode addSubreport's SubreportName/Name split produced.
    /// </summary>
    [Fact]
    public void Apply_AddsAGroupAndCrystalNamesItsSectionsAsTheValidatorPredicts()
    {
        var plan = new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = CardCode } }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            schema.Groups.Should().ContainSingle();
            schema.Groups[0].FieldRef.Should().Be(CardCode);
            schema.Groups[0].HeaderSection.Should().Be(GroupSectionNaming.HeaderSection(CardCode));
            schema.Groups[0].FooterSection.Should().Be(GroupSectionNaming.FooterSection(CardCode));

            schema.Sections.Should().Contain(s => s.Kind == "GroupHeader");
            schema.Sections.Should().Contain(s => s.Kind == "GroupFooter");
            FindSection(schema, GroupSectionNaming.HeaderSection(CardCode)).Kind.Should().Be("GroupHeader");
            FindSection(schema, GroupSectionNaming.FooterSection(CardCode)).Kind.Should().Be("GroupFooter");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// Two groups in one plan, indexed against the cumulative simulation. Also pins the ORDER:
    /// groups[0] is the outer group, and its footer section is the LAST group footer in the
    /// document -- the mapping ReportReader has to get right to report the two names at all.
    /// </summary>
    [Fact]
    public void Apply_AddsTwoGroupsAndBothReadBackInOrder()
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = CardCode, GroupIndex = 0 },
                new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = CardName, GroupIndex = 1 }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            schema.Groups.Should().HaveCount(2);
            schema.Groups[0].FieldRef.Should().Be(CardCode);
            schema.Groups[1].FieldRef.Should().Be(CardName);
            schema.Groups[0].HeaderSection.Should().Be(GroupSectionNaming.HeaderSection(CardCode));
            schema.Groups[1].HeaderSection.Should().Be(GroupSectionNaming.HeaderSection(CardName));
            schema.Groups[0].FooterSection.Should().Be(GroupSectionNaming.FooterSection(CardCode));
            schema.Groups[1].FooterSection.Should().Be(GroupSectionNaming.FooterSection(CardName));

            // Group headers print outermost-first, group footers outermost-LAST.
            var bands = schema.Sections.Select(s => s.Name).ToList();
            bands.IndexOf(schema.Groups[0].HeaderSection).Should().BeLessThan(bands.IndexOf(schema.Groups[1].HeaderSection));
            bands.IndexOf(schema.Groups[1].FooterSection).Should().BeLessThan(bands.IndexOf(schema.Groups[0].FooterSection));
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// Asserts the SAME property addSort writes -- SortInfo.Direction, read back off
    /// DataDefinition.Sorts after save and reopen. Both directions, because a test that only ever
    /// asks for the value Crystal defaults to could never fail.
    /// </summary>
    [Theory]
    [InlineData(SortDirections.Ascending)]
    [InlineData(SortDirections.Descending)]
    public void Apply_AddsASortAndItsDirectionSurvivesSaveAndReopen(string direction)
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.AddSort, FieldRef = CardName, Direction = direction }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            schema.Sorts.Should().ContainSingle();
            schema.Sorts[0].FieldRef.Should().Be(CardName);
            schema.Sorts[0].Direction.Should().Be(direction);
            schema.Groups.Should().BeEmpty(because: "a sort is not a group");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// A group's direction is not a property of the group -- ISCRGroupOptions carries none, so the
    /// applier reaches it through the sort Crystal creates for the grouped field. Descending
    /// specifically, because ascending is what Crystal would have produced anyway.
    /// </summary>
    [Fact]
    public void Apply_AddsADescendingGroupAndTheDirectionSurvivesSaveAndReopen()
    {
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddGroup, FieldRef = CardCode, Direction = SortDirections.Descending
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            schema.Groups.Should().ContainSingle();
            schema.Groups[0].Direction.Should().Be(SortDirections.Descending);
            // The group's own sort, which is where that direction actually lives.
            schema.Sorts.Should().ContainSingle();
            schema.Sorts[0].FieldRef.Should().Be(CardCode);
            schema.Sorts[0].Direction.Should().Be(SortDirections.Descending);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// The payoff of measuring the naming rule instead of guessing it: creating a group and
    /// placing content into the section it creates, in ONE plan. If the prediction were wrong this
    /// would fail at FindSection during the apply rather than at validation.
    /// </summary>
    [Fact]
    public void Apply_PlacesTextIntoAGroupHeaderCreatedEarlierInTheSamePlan()
    {
        var header = GroupSectionNaming.HeaderSection(CardCode);
        var plan = new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = CardCode },
                new LayoutOperation
                {
                    Action = LayoutActions.AddText, Section = header, NewName = "GroupTitle",
                    Text = "Customer", LeftTwips = 0, TopTwips = 0, WidthTwips = 2880, HeightTwips = 240
                }
            }
        };

        var schema = ApplyAndReread(plan, out var saved);
        try
        {
            var section = FindSection(schema, header);
            section.Kind.Should().Be("GroupHeader");
            var text = section.Objects.Single(o => o.Name == "GroupTitle");
            text.Kind.Should().Be("Text");
            text.Text.Should().Be("Customer");
            text.WidthTwips.Should().Be(2880);
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
    }

    /// <summary>
    /// A report with no groups must read back as EMPTY lists, not as a failed read -- the
    /// defensive contract every other optional part of the schema follows.
    /// </summary>
    [Fact]
    public void Read_ReportsEmptyGroupsAndSortsForAnUngroupedReport()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);
        var schema = ReportReader.Read(session);

        schema.Groups.Should().NotBeNull().And.BeEmpty();
        schema.Sorts.Should().NotBeNull().And.BeEmpty();
    }
}
