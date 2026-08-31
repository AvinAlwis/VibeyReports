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
}
