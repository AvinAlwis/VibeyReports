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
                    new ObjectInfo { Name = "Wide", Kind = "Text", LeftTwips = 9000, TopTwips = 500, WidthTwips = 3000, HeightTwips = 100 },
                    // Kind = "FieldHeading": what ReportReader emits for a wizard-generated column
                    // heading. FieldHeadingObjectClass implements ISCRTextObject and genuinely
                    // carries a font (final review F3).
                    new ObjectInfo { Name = "StageNameHeading", Kind = "FieldHeading", LeftTwips = 0, TopTwips = 100, WidthTwips = 2000, HeightTwips = 240 }
                }
            },
            new SectionInfo
            {
                Name = "Section3", Kind = "Details", HeightTwips = 400,
                Objects = { new ObjectInfo { Name = "CustomerName", Kind = "Field", LeftTwips = 300, TopTwips = 20, WidthTwips = 2500, HeightTwips = 300, DataSource = "{Customer.Name}" } }
            }
        },
        AvailableFields =
        {
            new FieldInfo { Name = "stage_name", FormulaForm = "{Command.stage_name}",
                            TableAlias = "Command", ValueType = "String", HeadingText = "Stage Name" }
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

    // Final review F3: FontableKinds previously omitted "FieldHeading", the kind ReportReader
    // emits for a wizard-generated column heading (FieldHeadingObjectClass implements
    // ISCRTextObject and genuinely carries a font), so setFont/setFontSize/setBold on a column
    // heading was wrongly rejected with a message claiming it "has no font to change."
    [Fact]
    public void Validate_AcceptsFontOperationsOnAFieldHeadingObject()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.SetBold, Target = "StageNameHeading", Bold = true },
            new LayoutOperation { Action = LayoutActions.SetFont, Target = "StageNameHeading", FontName = "Arial" },
            new LayoutOperation { Action = LayoutActions.SetFontSize, Target = "StageNameHeading", FontSizePt = 10f });

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

    // --- Task 6b: addField ---

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

    /// <summary>
    /// Round-1 fix (T2): fail closed on an empty allowlist. ReportReader's read-time catch clears
    /// AvailableFields to empty when a report's data source needs a logon it can't complete --
    /// exactly the state a regression that treated "no entries configured, so unknown, so allow"
    /// would need to slip past, since every other addField test in this file uses a populated list
    /// with a non-matching entry rather than an empty one.
    /// </summary>
    [Fact]
    public void Validate_RejectsAddFieldWhenAvailableFieldsIsEmpty()
    {
        var schema = Schema();
        schema.AvailableFields.Clear();

        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddField, Section = "Section3", NewName = "fStageName",
            FieldRef = "{Command.stage_name}",
            LeftTwips = 0, TopTwips = 20, WidthTwips = 2500, HeightTwips = 260
        });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("not a field in this report");
    }

    // --- addSubreport / setSubreportLink ---

    [Fact]
    public void Validate_AcceptsAddSubreportWithValidInput()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSubreport, Section = "Section3", NewName = "GoalDetail",
            ReportPath = @"C:\reports\GoalDetail.rpt",
            LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsAddSubreportWithoutReportPath()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSubreport, Section = "Section3", NewName = "GoalDetail",
            LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("reportPath");
    }

    [Fact]
    public void Validate_RejectsAddSubreportWhoseReportPathDoesNotEndInRpt()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSubreport, Section = "Section3", NewName = "GoalDetail",
            ReportPath = @"C:\reports\GoalDetail.txt",
            LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain(".rpt");
    }

    [Fact]
    public void Validate_RejectsAddSubreportWhoseNewNameCollides()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSubreport, Section = "Section3", NewName = "CustomerName",
            ReportPath = @"C:\reports\GoalDetail.rpt",
            LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("already exists");
    }

    [Fact]
    public void Validate_RejectsAddSubreportWithOutOfBoundsGeometry()
    {
        // printable width = 12240 - 720 - 720 = 10800; right edge here = 9000 + 3000 = 12000.
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSubreport, Section = "Section3", NewName = "GoalDetail",
            ReportPath = @"C:\reports\GoalDetail.rpt",
            LeftTwips = 9000, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("printable width");
    }

    [Fact]
    public void Validate_AcceptsSetSubreportLinkAgainstASubreportAddedEarlierInThePlan()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddSubreport, Section = "Section3", NewName = "GoalDetail",
                ReportPath = @"C:\reports\GoalDetail.rpt",
                LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
            },
            new LayoutOperation
            {
                Action = LayoutActions.SetSubreportLink, Target = "GoalDetail",
                MainReportField = "{sp_perf_goal_align_cascade;1.performance_cycle_id}",
                SubreportField = "{sp_goal_detail;1.performance_cycle_id}",
                LinkedParameter = "@performance_cycle_id"
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsSetSubreportLinkTargetingATextObject()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSubreportLink, Target = "Title",
            MainReportField = "{sp_x;1.performance_cycle_id}",
            SubreportField = "{sp_y;1.performance_cycle_id}",
            LinkedParameter = "@performance_cycle_id"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("not a Subreport");
    }

    [Fact]
    public void Validate_RejectsSetSubreportLinkMissingAnyLinkField()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddSubreport, Section = "Section3", NewName = "GoalDetail",
                ReportPath = @"C:\reports\GoalDetail.rpt",
                LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
            },
            new LayoutOperation
            {
                Action = LayoutActions.SetSubreportLink, Target = "GoalDetail",
                MainReportField = "", SubreportField = "{sp_y;1.performance_cycle_id}", LinkedParameter = "@performance_cycle_id"
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("mainReportField");
    }

    [Fact]
    public void Validate_RejectsSetFontSizeOnASubreport()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddSubreport, Section = "Section3", NewName = "GoalDetail",
                ReportPath = @"C:\reports\GoalDetail.rpt",
                LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
            },
            new LayoutOperation { Action = LayoutActions.SetFontSize, Target = "GoalDetail", FontSizePt = 10f });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("has no font to change");
    }

    // --- removeObject ---

    [Fact]
    public void Validate_RejectsRemoveObjectWithNoTarget()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveObject });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("target");
    }

    [Fact]
    public void Validate_RejectsRemoveObjectTargetingAnObjectThatDoesNotExist()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "NoSuchObject" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("NoSuchObject");
    }

    [Fact]
    public void Validate_AcceptsAValidRemoveObject()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "Title" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsAMoveTargetingAnObjectRemovedEarlierInThePlanAndSaysSo()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "Title" },
            new LayoutOperation { Action = LayoutActions.Move, Target = "Title", LeftTwips = 10, TopTwips = 10 });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        result.Errors[0].OperationIndex.Should().Be(1);
        result.Errors[0].Message.Should().Contain("Title");
        result.Errors[0].Message.Should().Contain("removed earlier in this plan",
            because: "the message must make clear Title was removed by this plan, not that it never existed");
    }

    [Fact]
    public void Validate_AcceptsAddTextReusingTheNameOfAnObjectRemovedEarlierInThePlan()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "Title" },
            new LayoutOperation
            {
                Action = LayoutActions.AddText, Section = "Section1", NewName = "Title", Text = "New Title",
                LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 300
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsResizeSectionShrinkAfterRemovingTheOnlyObjectThatWouldHaveBlockedIt()
    {
        // CustomerName ends at Top(20) + Height(300) = 320, so shrinking Section3 (currently 400)
        // to anything below 320 would normally be rejected -- unless the object is gone first.
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "CustomerName" },
            new LayoutOperation { Action = LayoutActions.ResizeSection, Section = "Section3", HeightTwips = 100 });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void LayoutActionsAll_HasEighteenEntriesIncludingSubreportActions()
    {
        LayoutActions.All.Should().HaveCount(18);
        LayoutActions.All.Should().Contain(LayoutActions.RemoveObject);
        LayoutActions.All.Should().Contain(LayoutActions.SetTextColor);
        LayoutActions.All.Should().Contain(LayoutActions.SetFillColor);
        LayoutActions.All.Should().Contain(LayoutActions.SetLineColor);
        LayoutActions.All.Should().Contain(LayoutActions.SetSectionBackground);
        LayoutActions.All.Should().Contain(LayoutActions.AddSubreport);
        LayoutActions.All.Should().Contain(LayoutActions.SetSubreportLink);
    }

    // --- colour operations ---

    [Fact]
    public void Validate_AcceptsSetTextColorOnAFontableObject()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetTextColor, Target = "Title", Color = "#1F2A37" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsSetTextColorOnAFieldHeadingObject()
    {
        // Reuses IsFontable, which already includes FieldHeading.
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetTextColor, Target = "StageNameHeading", Color = "#1F2A37" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsSetTextColorOnALine()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetTextColor, Target = "HeaderRule", Color = "#1F2A37" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("HeaderRule");
    }

    [Fact]
    public void Validate_RejectsSetFillColorOnAText()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetFillColor, Target = "Title", Color = "#1F2A37" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("Box");
    }

    [Fact]
    public void Validate_RejectsSetLineColorOnAField()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetLineColor, Target = "CustomerName", Color = "#1F2A37" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("Line or Box");
    }

    [Fact]
    public void Validate_AcceptsSetLineColorOnALine()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetLineColor, Target = "HeaderRule", Color = "#1F2A37" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsSetSectionBackgroundOnAnExistingSection()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetSectionBackground, Section = "Section1", Color = "#1F2A37" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsSetSectionBackgroundOnAnUnknownSection()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetSectionBackground, Section = "SectionZ", Color = "#1F2A37" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("SectionZ");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1F2A37")]
    [InlineData("#1F2A3")]
    [InlineData("#1F2A377")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void Validate_RejectsMalformedColorWithAMessageShowingTheExpectedForm(string? color)
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetTextColor, Target = "Title", Color = color });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("#RRGGBB");
    }

    [Fact]
    public void Validate_AcceptsAddBoxThenSetFillColorOnTheNewBoxInTheSamePlan()
    {
        // Kind checks must use the SIMULATED kind: a box added earlier in this same plan must be
        // a valid target for setFillColor even though it doesn't exist in the original schema.
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddBox, Section = "Section1", NewName = "NewBox1",
                LeftTwips = 0, TopTwips = 0, WidthTwips = 100, HeightTwips = 100
            },
            new LayoutOperation { Action = LayoutActions.SetFillColor, Target = "NewBox1", Color = "#1F2A37" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsAddLineThenSetLineColorOnTheNewLineInTheSamePlan()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddLine, Section = "Section1", NewName = "NewLine1",
                LeftTwips = 0, TopTwips = 0, WidthTwips = 100, HeightTwips = 0
            },
            new LayoutOperation { Action = LayoutActions.SetLineColor, Target = "NewLine1", Color = "#1F2A37" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsAddTextThenSetTextColorOnTheNewTextInTheSamePlan()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddText, Section = "Section1", NewName = "NewText1", Text = "hi",
                LeftTwips = 0, TopTwips = 0, WidthTwips = 100, HeightTwips = 50
            },
            new LayoutOperation { Action = LayoutActions.SetTextColor, Target = "NewText1", Color = "#1F2A37" });

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
