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
            added!.Kind.Should().Be("Subreport");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }
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
}
