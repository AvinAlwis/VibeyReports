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
                Objects =
                {
                    new ObjectInfo { Name = "Title", Kind = "Text", LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 300 },
                    // Kind = "Line": what ReportReader emits for a Crystal line, and what addLine creates. Used by F1/T4.
                    new ObjectInfo { Name = "HeaderRule", Kind = "Line", LeftTwips = 0, TopTwips = 350, WidthTwips = 3000, HeightTwips = 0 },
                    // Already overflowing the printable width (12240 - 720 - 720 = 10800; right edge = 9000 + 3000 = 12000). Used by F4/T1/T8.
                    new ObjectInfo { Name = "Wide", Kind = "Text", LeftTwips = 9000, TopTwips = 500, WidthTwips = 3000, HeightTwips = 100 }
                }
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

    // --- Round 1 fix regression tests (T1-T9) ---

    [Fact]
    public void Validate_LeavesStateUntouchedWhenAnOperationIsRejected()
    {
        // "Wide" already overflows (right edge 12000 > printable width 10800). Op 0 is an
        // invalid move (negative leftTwips) that must be rejected AND must not mutate the
        // simulated position. Op 1 only passes the "don't make it worse" check in move if
        // op 0's rejected leftTwips=-50000 was never applied to Wide's simulated Left —
        // if it had been, Wide's baseline right edge would look wildly negative and op 1's
        // real move (which is still an improvement over the true original position) would
        // wrongly appear to make things worse and get rejected too.
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.Move, Target = "Wide", LeftTwips = -50000, TopTwips = 500 },
            new LayoutOperation { Action = LayoutActions.Move, Target = "Wide", LeftTwips = 8000, TopTwips = 500 });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.Errors.Should().ContainSingle(because: string.Join("; ", result.Errors.ConvertAll(e => $"[{e.OperationIndex}] {e.Message}")));
        result.Errors[0].OperationIndex.Should().Be(0);
    }

    [Fact]
    public void Validate_RejectsResizeSectionThatWouldClipAnObjectAddedEarlierInThePlan()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddText, Section = "Section1", NewName = "Note1", Text = "hi",
                LeftTwips = 0, TopTwips = 500, WidthTwips = 100, HeightTwips = 200
            },
            new LayoutOperation { Action = LayoutActions.ResizeSection, Section = "Section1", HeightTwips = 600 });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        result.Errors[0].OperationIndex.Should().Be(1);
        result.Errors[0].Message.Should().Contain("Note1");
    }

    [Fact]
    public void Validate_AcceptsAddHappyPathIncludingZeroWidthAndZeroHeightRules()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddText, Section = "Section1", NewName = "Note2", Text = "hi",
                LeftTwips = 0, TopTwips = 0, WidthTwips = 100, HeightTwips = 50
            },
            // Horizontal rule: heightTwips = 0.
            new LayoutOperation
            {
                Action = LayoutActions.AddLine, Section = "Section1", NewName = "HRule1",
                LeftTwips = 0, TopTwips = 600, WidthTwips = 1000, HeightTwips = 0
            },
            // Vertical rule: widthTwips = 0.
            new LayoutOperation
            {
                Action = LayoutActions.AddLine, Section = "Section1", NewName = "VRule1",
                LeftTwips = 0, TopTwips = 600, WidthTwips = 0, HeightTwips = 100
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsFontOperationsOnANonTextNonFieldObject()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.SetBold, Target = "HeaderRule", Bold = true },
            new LayoutOperation { Action = LayoutActions.SetFont, Target = "HeaderRule", FontName = "Arial" },
            new LayoutOperation { Action = LayoutActions.SetFontSize, Target = "HeaderRule", FontSizePt = 10f });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(3);
        result.Errors.Should().OnlyContain(e => e.Message.Contains("HeaderRule") && e.Message.Contains("Line"));
    }

    [Fact]
    public void Validate_AcceptsSetBoldOnATextObject()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetBold, Target = "Title", Bold = true });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsActionWithWrongCasing()
    {
        var plan = PlanOf(new LayoutOperation { Action = "Move", Target = "CustomerName", LeftTwips = 10, TopTwips = 10 });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("not a supported action");
    }

    [Fact]
    public void Validate_RejectsNewNameCollidingWithAnObjectAddedEarlierInThePlan()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddText, Section = "Section1", NewName = "Dup1", Text = "hi",
                LeftTwips = 0, TopTwips = 0, WidthTwips = 100, HeightTwips = 50
            },
            new LayoutOperation
            {
                Action = LayoutActions.AddText, Section = "Section1", NewName = "Dup1", Text = "again",
                LeftTwips = 0, TopTwips = 100, WidthTwips = 100, HeightTwips = 50
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        result.Errors[0].OperationIndex.Should().Be(1);
        result.Errors[0].Message.Should().Contain("already exists");
    }

    [Fact]
    public void Validate_RejectsLeftTwipsThatWouldOverflowIntArithmetic()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.Move, Target = "CustomerName", LeftTwips = 2147483000, TopTwips = 0
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("printable width");
    }

    [Fact]
    public void Validate_AllowsPureVerticalMoveOnAnAlreadyOverflowingObject()
    {
        // Same leftTwips as "Wide" already has (9000) — a purely vertical move.
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.Move, Target = "Wide", LeftTwips = 9000, TopTwips = 600
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AllowsMoveThatReducesButDoesNotEliminateOverflowOnAnAlreadyOverflowingObject()
    {
        // Right edge goes from 12000 to 11000 - still past the 10800 printable width, but an improvement.
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.Move, Target = "Wide", LeftTwips = 8000, TopTwips = 500
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsMoveThatWorsensOverflowOnAnAlreadyOverflowingObject()
    {
        // Right edge goes from 12000 to 12500 - a strict regression, must be rejected.
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.Move, Target = "Wide", LeftTwips = 9500, TopTwips = 500
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("printable width");
    }

    [Fact]
    public void Validate_ReturnsInvalidRatherThanThrowingWhenOperationsIsNull()
    {
        var plan = new LayoutPlan { PlanVersion = 1, Operations = null! };

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ReturnsInvalidRatherThanThrowingWhenAnOperationElementIsNull()
    {
        var plan = new LayoutPlan { PlanVersion = 1, Operations = { null! } };

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
    }

    // --- Round 2 fix (F1): RAS only supports horizontal or vertical lines ---

    [Fact]
    public void Validate_RejectsAddLineWithBothAxesNonZero()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddLine, Section = "Section1", NewName = "DiagonalRule",
            LeftTwips = 0, TopTwips = 0, WidthTwips = 2880, HeightTwips = 340
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("horizontal or vertical");
    }

    [Fact]
    public void Validate_RejectsResizeOfALineToBothAxesNonZero()
    {
        // HeaderRule is a Line (WidthTwips = 3000, HeightTwips = 0) in the fixed schema above.
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.Resize, Target = "HeaderRule", WidthTwips = 2880, HeightTwips = 340
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("horizontal or vertical");
    }

    [Fact]
    public void Validate_AcceptsResizeOfALineThatStaysHorizontalOrVertical()
    {
        var plan = PlanOf(
            // Stays horizontal.
            new LayoutOperation { Action = LayoutActions.Resize, Target = "HeaderRule", WidthTwips = 2000, HeightTwips = 0 });

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
