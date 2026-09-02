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

    /// <summary>
    /// Schema() plus a sub-report ALREADY embedded in the report, shaped exactly as ReportReader
    /// emits one (measured against out/reports/PMSV10_GoalAlignCascade.subreport.rpt): the placed
    /// object's Name is Crystal's own auto-numbered "Subreport1", while the sub-report's own name
    /// -- the only string setSubreportLink resolves by -- is the separate SubreportName.
    /// Kept out of Schema() so the shared fixture's object counts stay as every other test found
    /// them.
    /// </summary>
    private static ReportSchema SchemaWithEmbeddedSubreport()
    {
        var schema = Schema();
        schema.Sections[1].Objects.Add(new ObjectInfo
        {
            Name = "Subreport1", Kind = "Subreport", SubreportName = "GoalDetail",
            LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300,
            SubreportLinks = new System.Collections.Generic.List<SubreportLinkInfo>()
        });
        return schema;
    }

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

    /// <summary>
    /// A sub-report is not fontable. Targeted through the ALREADY-EMBEDDED sub-report's placed
    /// object name, which is the only way this rule is reachable on its own: a sub-report added by
    /// the same plan is now stopped one step earlier, by F1's gate (see the test below), so
    /// pointing this at one would assert the wrong rule's message.
    /// </summary>
    [Fact]
    public void Validate_RejectsSetFontSizeOnASubreport()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetFontSize, Target = "Subreport1", FontSizePt = 10f
        });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithEmbeddedSubreport());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("has no font to change");
    }

    /// <summary>
    /// F1 again, for a font operation: setFontSize resolves through LayoutApplier.FindObject just
    /// like move/resize do, so a same-plan sub-report must be stopped by the identity gate rather
    /// than reach the applier. Both rules reject it; only this one explains why the name does not
    /// resolve.
    /// </summary>
    [Fact]
    public void Validate_RejectsSetFontSizeOnASubreportAddedEarlierInTheSamePlan()
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
        result.Errors[0].Message.Should().Contain("added earlier in this same plan");
    }

    // --- review round 3: the SubreportName/Name identity split ---

    /// <summary>
    /// F1. Before the fix this plan validated CLEAN: the added sub-report was registered under
    /// newName, so "move GoalDetail" resolved against the simulation happily. At apply time
    /// operation 0 was written to the LIVE document, then operation 1 threw from
    /// LayoutApplier.FindObject -- which matches on the report object's own Name, and Crystal had
    /// named the placed object "Subreport1", not "GoalDetail". That faulted the session and lost
    /// the whole plan, addSubreport included. It must be rejected before anything is written, and
    /// the message must give the reason (Crystal names the object itself) rather than a bare
    /// "does not exist", which would send the agent hunting for a typo it did not make.
    /// </summary>
    [Theory]
    [InlineData(LayoutActions.Move)]
    [InlineData(LayoutActions.Resize)]
    [InlineData(LayoutActions.RemoveObject)]
    [InlineData(LayoutActions.SetAlignment)]
    public void Validate_RejectsNonLinkOperationsAgainstASubreportAddedEarlierInTheSamePlan(string action)
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
                Action = action, Target = "GoalDetail",
                LeftTwips = 10, TopTwips = 10, WidthTwips = 100, HeightTwips = 100, Alignment = "Left"
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        result.Errors[0].OperationIndex.Should().Be(1);
        result.Errors[0].Message.Should().Contain("added earlier in this same plan")
              .And.Contain("auto-numbered")
              .And.Contain("Only setSubreportLink");
    }

    /// <summary>
    /// F1's counterpart: the gate must not cost the normal usage anything. setSubreportLink is the
    /// one action that CAN address a same-plan sub-report by newName, because SetSubreportLinks is
    /// keyed by SubreportName rather than by the report object. Verified live against the real
    /// worker (addSubreport + setSubreportLink in one plan, ok:true).
    /// </summary>
    [Fact]
    public void Validate_StillAcceptsSetSubreportLinkAgainstASubreportAddedEarlierInTheSamePlan()
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
                SubreportField = "{sp_goal_detail;1.goal_id}"
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// F2, the finding that matters most: linking a sub-report that is ALREADY embedded -- put
    /// there by an earlier apply_layout call, or present in the source .rpt from the start. Before
    /// the fix this was impossible to express at all: read_report never reported SubreportName, so
    /// the only sub-report name the agent could see was the placed object's ("Subreport1"), which
    /// setSubreportLink cannot resolve. Verified end-to-end against the real worker and
    /// out/reports/PMSV10_GoalAlignCascade.subreport.rpt: ok:true, and the link reads back.
    /// </summary>
    [Fact]
    public void Validate_AcceptsSetSubreportLinkAgainstASubreportAlreadyEmbeddedInTheReport()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSubreportLink, Target = "GoalDetail",
            MainReportField = "{sp_perf_goal_align_cascade;1.performance_cycle_id}",
            SubreportField = "{sp_perf_goal_align_detail;1.goal_id}"
        });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithEmbeddedSubreport());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// F2. The single likeliest mistake now that both names are visible: passing the object "name"
    /// where the "subreportName" belongs. Measured: that reaches SetSubreportLinks and fails with
    /// COM "This value is write-only." -- true, loud, and useless. Catch it in the validator and
    /// name the string that would have worked.
    /// </summary>
    [Fact]
    public void Validate_RejectsSetSubreportLinkTargetingTheSubreportsPlacedObjectNameAndNamesTheRightOne()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSubreportLink, Target = "Subreport1",
            MainReportField = "{sp_perf_goal_align_cascade;1.performance_cycle_id}",
            SubreportField = "{sp_perf_goal_align_detail;1.goal_id}"
        });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithEmbeddedSubreport());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("placed object name")
              .And.Contain("subreportName")
              .And.Contain("\"GoalDetail\"");
    }

    [Fact]
    public void Validate_RejectsSetSubreportLinkAgainstAnUnknownSubreportName()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSubreportLink, Target = "NoSuchSubreport",
            MainReportField = "{sp_x;1.a}", SubreportField = "{sp_y;1.a}"
        });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithEmbeddedSubreport());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("No sub-report named \"NoSuchSubreport\"");
    }

    /// <summary>
    /// F5. newName uniqueness was checked against the report OBJECT names only, so an addSubreport
    /// named for a sub-report the report already embeds collided invisibly -- and setSubreportLink,
    /// which resolves through that same name-space, would then have two candidates and no way to
    /// say which was meant. Verified live: rejected with this message.
    /// </summary>
    [Fact]
    public void Validate_RejectsAddSubreportWhoseNewNameCollidesWithAnAlreadyEmbeddedSubreportName()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSubreport, Section = "Section3", NewName = "GoalDetail",
            ReportPath = @"C:\reports\GoalDetail.rpt",
            LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
        });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithEmbeddedSubreport());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("already embedded");
    }

    /// <summary>
    /// Removing a sub-report's placed container object takes the embedded sub-report with it, so a
    /// later setSubreportLink against its SubreportName must stop resolving. (removeObject targets
    /// the object name, which is legal here because this sub-report was embedded by an earlier
    /// plan, not by this one.)
    /// </summary>
    [Fact]
    public void Validate_RejectsSetSubreportLinkAgainstASubreportRemovedEarlierInThePlan()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "Subreport1" },
            new LayoutOperation
            {
                Action = LayoutActions.SetSubreportLink, Target = "GoalDetail",
                MainReportField = "{sp_x;1.a}", SubreportField = "{sp_y;1.a}"
            });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithEmbeddedSubreport());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("No sub-report named \"GoalDetail\"");
    }

    /// <summary>
    /// F4. linkedParameter is optional because it has no effect: measured against the installed
    /// 11.5 RAS, Crystal discards whatever LinkedParameterName is written and substitutes its own
    /// "{?Pm-&lt;mainReportField&gt;}". Requiring a value only invited the agent to invent a stored
    /// procedure parameter name and believe it had been wired up. Verified live: a plan omitting it
    /// entirely applies ok:true and reads back the Pm- substitution.
    /// </summary>
    [Fact]
    public void Validate_AcceptsSetSubreportLinkWithNoLinkedParameter()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSubreportLink, Target = "GoalDetail",
            MainReportField = "{sp_perf_goal_align_cascade;1.performance_cycle_id}",
            SubreportField = "{sp_perf_goal_align_detail;1.goal_id}",
            LinkedParameter = null
        });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithEmbeddedSubreport());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// F7. The tool contract promises an absolute reportPath and nothing checked it, so a relative
    /// path silently resolved against the WORKER process's working directory -- a path the agent
    /// has no visibility of, making even the applier's "file not found" message name something the
    /// agent never wrote. Path.IsPathRooted is pure string arithmetic, so this stays a validator
    /// rule and the validator stays free of file I/O.
    /// </summary>
    [Theory]
    [InlineData(@"reports\GoalDetail.rpt")]
    [InlineData("GoalDetail.rpt")]
    [InlineData("./GoalDetail.rpt")]
    public void Validate_RejectsAddSubreportWithARelativeReportPath(string reportPath)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSubreport, Section = "Section3", NewName = "GoalDetail",
            ReportPath = reportPath,
            LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors[0].Message.Should().Contain("must be an absolute path");
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

    // The contents of LayoutActions.All are asserted exhaustively by
    // LayoutPlanTests.LayoutActions_All_ContainsEverySupportedActionAndNothingElse, which compares
    // against the complete set. A second, weaker copy used to live here, asserting a hardcoded
    // count plus a hand-picked subset. It went red on every addition without ever catching
    // anything the exhaustive test would have missed, so it was deleted rather than renumbered.

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

    // ---- data-source table operations ------------------------------------
    //
    // Shaped after out/reports/PMSV10_GoalAlignCascade.rpt, which is bound to three stored
    // procedures. "sp_perf_goal_align_detail;1" is the redundant one -- two unlinked procedures in
    // one report is a cartesian join -- and five Field objects (DR0..DR4 in the real report, two
    // here) are bound to it. Measured against the real SDK: Crystal's own RemoveTable removes such
    // a table WITHOUT complaint, leaving those fields unresolvable, so this validator is the only
    // thing standing between a model and a broken report.

    private const string Cascade = "sp_perf_goal_align_cascade;1";
    private const string Detail = "sp_perf_goal_align_detail;1";

    private static ReportSchema TableSchema()
    {
        var schema = Schema();
        schema.Sections[1].Objects.Add(new ObjectInfo
        {
            Name = "DR0", Kind = "Field", LeftTwips = 0, TopTwips = 0, WidthTwips = 500, HeightTwips = 200,
            DataSource = "{" + Detail + ".goal_id}"
        });
        schema.Sections[1].Objects.Add(new ObjectInfo
        {
            Name = "DR1", Kind = "Field", LeftTwips = 600, TopTwips = 0, WidthTwips = 500, HeightTwips = 200,
            DataSource = "{" + Detail + ".goal_name}"
        });
        schema.Sections[0].Objects.Add(new ObjectInfo
        {
            Name = "B1L0v", Kind = "Field", LeftTwips = 0, TopTwips = 600, WidthTwips = 500, HeightTwips = 100,
            DataSource = "{" + Cascade + ".employee_name}"
        });
        schema.AvailableFields.Add(new FieldInfo
        {
            Name = "goal_id", FormulaForm = "{" + Detail + ".goal_id}",
            TableAlias = Detail, ValueType = "Number", HeadingText = "Goal Id"
        });
        schema.AvailableFields.Add(new FieldInfo
        {
            Name = "employee_name", FormulaForm = "{" + Cascade + ".employee_name}",
            TableAlias = Cascade, ValueType = "String", HeadingText = "Employee Name"
        });
        return schema;
    }

    [Fact]
    public void Validate_AcceptsRemoveTableWhenNothingIsBoundToTheAlias()
    {
        var schema = TableSchema();
        // "Command" is a real alias in the shared fixture with no object bound to it.
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = "Command" });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsRemoveTableWhileABoundFieldSurvives_AndNamesTheObjects()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = Detail });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("DR0").And.Contain("DR1");
    }

    /// <summary>
    /// The actual use case, and the reason the simulation has to be cumulative: strip the bound
    /// fields, then drop the table they came from, in one plan.
    /// </summary>
    [Fact]
    public void Validate_AcceptsRemoveTableWhenThePlanRemovesTheBoundFieldsFirst()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "DR0" },
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "DR1" },
            new LayoutOperation { Action = LayoutActions.RemoveTable, Target = Detail });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// Removing only SOME of the bound fields must still be refused, or the "cumulative" handling
    /// above would just be a blanket exemption for any plan that happens to contain a removeObject.
    /// </summary>
    [Fact]
    public void Validate_RejectsRemoveTableWhenOnlySomeBoundFieldsAreRemovedFirst()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "DR0" },
            new LayoutOperation { Action = LayoutActions.RemoveTable, Target = Detail });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("DR1").And.NotContain("DR0");
    }

    [Fact]
    public void Validate_RejectsRemoveTableWithNoTarget()
    {
        foreach (var target in new string?[] { null, "", "   " })
        {
            var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = target });

            var result = LayoutPlanValidator.Validate(plan, TableSchema());

            result.IsValid.Should().BeFalse();
            result.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires \"target\"");
        }
    }

    /// <summary>
    /// ReportReader deliberately clears AvailableFields when field enumeration cannot reach the
    /// database -- a normal, supported state. Absence cannot be proven from an empty list, so the
    /// existence check is skipped rather than rejecting every table operation.
    /// </summary>
    [Fact]
    public void Validate_AcceptsTableOperationsWhenAvailableFieldsIsEmpty()
    {
        var schema = TableSchema();
        schema.AvailableFields.Clear();

        // removeTable LAST on purpose: once an alias has been removed by the plan, referring to it
        // again is a plan error even with an unknown alias set, and that ordering rule is correct.
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddTable, Target = "who_knows;1", TableName = "sp_y;1", NewName = "sp_y;1"
            },
            new LayoutOperation { Action = LayoutActions.SetTableLocation, Target = "who_knows;1", TableName = "sp_x;1" },
            new LayoutOperation { Action = LayoutActions.RemoveTable, Target = "who_knows;1" });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// Rule 1 still bites with an empty AvailableFields: the bound-object check reads the report's
    /// own objects, not the field list, so a missing database connection must not turn the one
    /// safety rule off.
    /// </summary>
    [Fact]
    public void Validate_StillRejectsARemoveTableWithBoundObjectsWhenAvailableFieldsIsEmpty()
    {
        var schema = TableSchema();
        schema.AvailableFields.Clear();

        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = Detail });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("DR0");
    }

    /// <summary>
    /// A sub-report carries its OWN data source and is never bound to a main report's tables, so
    /// its DataSource is null. Without this pinned, embedding a sub-report could silently make its
    /// host's tables unremovable.
    /// </summary>
    [Fact]
    public void Validate_ASubreportDoesNotBlockRemoveTable()
    {
        var schema = TableSchema();
        schema.Sections[1].Objects.Add(new ObjectInfo
        {
            Name = "Subreport1", Kind = "Subreport", SubreportName = "GoalDetail",
            LeftTwips = 0, TopTwips = 20, WidthTwips = 3000, HeightTwips = 300
        });

        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = "Command" });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// A plain Contains would let alias "foo;1" match "{other_foo;1.x}" and refuse a legal
    /// removal. The match is the prefix "{" + alias + "." specifically.
    /// </summary>
    [Fact]
    public void Validate_AliasMatchingIsNotFooledByASubstringOfAnotherAlias()
    {
        var schema = Schema();
        schema.Sections[1].Objects.Add(new ObjectInfo
        {
            Name = "OtherField", Kind = "Field", LeftTwips = 0, TopTwips = 0, WidthTwips = 100, HeightTwips = 100,
            DataSource = "{other_foo;1.x}"
        });
        schema.AvailableFields.Add(new FieldInfo
        {
            Name = "x", FormulaForm = "{foo;1.x}", TableAlias = "foo;1", ValueType = "String"
        });
        schema.AvailableFields.Add(new FieldInfo
        {
            Name = "x", FormulaForm = "{other_foo;1.x}", TableAlias = "other_foo;1", ValueType = "String"
        });

        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = "foo;1" });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>The formula form is case-insensitive in Crystal; the match must be too.</summary>
    [Fact]
    public void Validate_AliasMatchingIsCaseInsensitive()
    {
        var schema = TableSchema();
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.RemoveTable, Target = "SP_PERF_GOAL_ALIGN_DETAIL;1"
        });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("DR0");
    }

    [Fact]
    public void Validate_RejectsRemoveTableForAnAliasThatIsNotInTheDataSource()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = "sp_nope;1" });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("Known aliases");
    }

    [Fact]
    public void Validate_AcceptsAddTableThenRemoveTableOfTheSameAlias()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddTable, Target = Cascade,
                TableName = "sp_perf_goal_align_history;1", NewName = "sp_perf_goal_align_history;1"
            },
            new LayoutOperation { Action = LayoutActions.RemoveTable, Target = "sp_perf_goal_align_history;1" });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsAnAddFieldReferencingATableRemovedEarlierInTheSamePlan()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "DR0" },
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "DR1" },
            new LayoutOperation { Action = LayoutActions.RemoveTable, Target = Detail },
            new LayoutOperation
            {
                Action = LayoutActions.AddField, Section = "Section3", NewName = "NewGoalId",
                FieldRef = "{" + Detail + ".goal_id}",
                LeftTwips = 0, TopTwips = 0, WidthTwips = 500, HeightTwips = 200
            });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("is not a field in this report's data source");
    }

    /// <summary>
    /// A field added by an earlier addField binds to a table just as a pre-existing one does, so
    /// the SimObject must carry its fieldRef as a DataSource or the removeTable check has a hole.
    /// </summary>
    [Fact]
    public void Validate_RejectsRemoveTableWhenAFieldAddedEarlierInThePlanIsBoundToIt()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "DR0" },
            new LayoutOperation { Action = LayoutActions.RemoveObject, Target = "DR1" },
            new LayoutOperation
            {
                Action = LayoutActions.AddField, Section = "Section3", NewName = "NewGoalId",
                FieldRef = "{" + Detail + ".goal_id}",
                LeftTwips = 0, TopTwips = 0, WidthTwips = 500, HeightTwips = 200
            },
            new LayoutOperation { Action = LayoutActions.RemoveTable, Target = Detail });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("NewGoalId");
    }

    [Fact]
    public void Validate_RejectsAddTableOnAnAliasCollision()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddTable, Target = Cascade, TableName = "sp_x;1", NewName = Detail
        });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("already in this report's data source");
    }

    [Fact]
    public void Validate_RejectsAddTableWithoutTableName()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddTable, Target = Cascade, NewName = "sp_new;1"
        });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires \"tableName\"");
    }

    [Fact]
    public void Validate_RejectsAddTableWithoutNewName()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddTable, Target = Cascade, TableName = "sp_new;1"
        });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires \"newName\"");
    }

    [Fact]
    public void Validate_RejectsAddTableWhoseSourceAliasDoesNotExist()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddTable, Target = "sp_nope;1", TableName = "sp_new;1", NewName = "sp_new;1"
        });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("Known aliases");
    }

    [Fact]
    public void Validate_RejectsSetTableLocationOnAnUnknownTarget()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetTableLocation, Target = "sp_nope;1", TableName = "sp_x;1"
        });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("Known aliases");
    }

    [Fact]
    public void Validate_RejectsSetTableLocationWithoutTableName()
    {
        foreach (var name in new string?[] { null, "", "  " })
        {
            var plan = PlanOf(new LayoutOperation
            {
                Action = LayoutActions.SetTableLocation, Target = Detail, TableName = name
            });

            var result = LayoutPlanValidator.Validate(plan, TableSchema());

            result.IsValid.Should().BeFalse();
            result.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires \"tableName\"");
        }
    }

    [Fact]
    public void Validate_AcceptsSetTableLocationOnAKnownTable()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetTableLocation, Target = Detail, TableName = "sp_perf_goal_align_detail_v2;1"
        });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// A table operation's target lives in the TABLE-ALIAS name-space, not the report-object one.
    /// If removeTable were wired into the shared needsTarget block it would resolve the alias
    /// against the object dictionary and reject every table operation with "Object ... does not
    /// exist in the report."
    /// </summary>
    [Fact]
    public void Validate_ResolvesATableTargetInTheTableNameSpaceNotTheObjectOne()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = "Command" });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// The converse: an OBJECT name must not accidentally resolve as a table alias.
    /// </summary>
    [Fact]
    public void Validate_RejectsARemoveTableTargetingAReportObjectName()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.RemoveTable, Target = "CustomerName" });

        var result = LayoutPlanValidator.Validate(plan, TableSchema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("Known aliases");
    }

    // ---- formatting operations -------------------------------------------------------------

    private static string Why(ValidationResult r) => string.Join("; ", r.Errors.ConvertAll(e => e.Message));

    [Fact]
    public void Validate_AcceptsASectionBreak()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSectionBreak, Section = "Section3", NewPageAfter = true
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    [Fact]
    public void Validate_RejectsASectionBreakWithNeitherBoolean()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetSectionBreak, Section = "Section3" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("newPageBefore");
    }

    [Fact]
    public void Validate_RejectsASectionBreakWithoutASection()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetSectionBreak, NewPageBefore = true });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires \"section\"");
    }

    [Fact]
    public void Validate_AcceptsAnAddSpecialField()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSpecialField, Section = "Section1", NewName = "Pager",
            SpecialType = SpecialFieldTypes.PageNOfM,
            LeftTwips = 0, TopTwips = 0, WidthTwips = 1440, HeightTwips = 240
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    [Theory]
    [InlineData("pageNumber")]
    [InlineData("pageNOfM")]
    [InlineData("totalPageCount")]
    [InlineData("printDate")]
    [InlineData("printTime")]
    [InlineData("reportTitle")]
    [InlineData("recordNumber")]
    public void Validate_AcceptsEverySupportedSpecialType(string specialType)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSpecialField, Section = "Section1", NewName = "Special",
            SpecialType = specialType,
            LeftTwips = 0, TopTwips = 0, WidthTwips = 1440, HeightTwips = 240
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    /// <summary>
    /// The allowlist IS the contract. Accepting an unknown string and letting it fail at the COM
    /// boundary would surface as a Crystal error naming an enum the caller never wrote -- including
    /// for the raw enum member names, which callers must not have to know.
    /// </summary>
    [Theory]
    [InlineData("crSpecialFieldTypePageNOfM")]
    [InlineData("groupNumber")]
    [InlineData("modificationDate")]
    [InlineData("")]
    public void Validate_RejectsAnUnknownSpecialType(string specialType)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSpecialField, Section = "Section1", NewName = "Pager",
            SpecialType = specialType,
            LeftTwips = 0, TopTwips = 0, WidthTwips = 1440, HeightTwips = 240
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("specialType");
    }

    [Fact]
    public void Validate_RejectsAnAddSpecialFieldWhoseNewNameAlreadyExists()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSpecialField, Section = "Section1", NewName = "Title",
            SpecialType = SpecialFieldTypes.PrintDate,
            LeftTwips = 0, TopTwips = 0, WidthTwips = 1440, HeightTwips = 240
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("already exists");
    }

    [Fact]
    public void Validate_RejectsAnAddSpecialFieldPastThePrintableWidth()
    {
        // printable width = 12240 - 720 - 720 = 10800.
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSpecialField, Section = "Section1", NewName = "Pager",
            SpecialType = SpecialFieldTypes.PageNumber,
            LeftTwips = 10000, TopTwips = 0, WidthTwips = 2000, HeightTwips = 240
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("printable width");
    }

    /// <summary>
    /// A special field IS a field object, so it must be fontable -- the whole point of registering
    /// its simulated kind as "Field". If this regressed to "Other", a page-number footer could be
    /// placed but never sized or emboldened.
    /// </summary>
    [Fact]
    public void Validate_AcceptsSetFontSizeAndSetBoldOnASpecialFieldAddedEarlierInThePlan()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddSpecialField, Section = "Section1", NewName = "Pager",
                SpecialType = SpecialFieldTypes.PageNOfM,
                LeftTwips = 0, TopTwips = 0, WidthTwips = 1440, HeightTwips = 240
            },
            new LayoutOperation { Action = LayoutActions.SetFontSize, Target = "Pager", FontSizePt = 9f },
            new LayoutOperation { Action = LayoutActions.SetBold, Target = "Pager", Bold = true });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    [Fact]
    public void Validate_AcceptsANumberFormatOnAField()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetNumberFormat, Target = "CustomerName",
            DecimalPlaces = 0, ThousandsSeparator = false
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    [Fact]
    public void Validate_RejectsANumberFormatWithNoOptionalFieldSupplied()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetNumberFormat, Target = "CustomerName"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("at least one");
    }

    [Theory]
    [InlineData("Title")]           // Text
    [InlineData("HeaderRule")]      // Line
    [InlineData("StageNameHeading")] // FieldHeading -- fontable, but has no ISCRFieldFormat
    public void Validate_RejectsANumberFormatOnANonFieldObject(string target)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetNumberFormat, Target = target, DecimalPlaces = 0
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("not a Field");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Validate_RejectsAnOutOfRangeDecimalPlaces(int decimalPlaces)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetNumberFormat, Target = "CustomerName", DecimalPlaces = decimalPlaces
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("between 0 and 10");
    }

    /// <summary>
    /// THE rule that keeps setNumberFormat usable off the VPN. A field's value type is knowable
    /// only from schema.AvailableFields, and ReportReader deliberately CLEARS that list whenever
    /// the data source cannot be enumerated -- a normal, supported state. A numeric-type check
    /// would therefore reject every setNumberFormat whenever the database is unreachable, which is
    /// exactly the trap removeTable's existence check already sidesteps.
    /// </summary>
    [Fact]
    public void Validate_AcceptsANumberFormatWhenAvailableFieldsIsEmpty()
    {
        var schema = Schema();
        schema.AvailableFields.Clear();

        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetNumberFormat, Target = "CustomerName", DecimalPlaces = 0
        });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    /// <summary>
    /// The same rule stated from the other side: the target Field's own declared value type is
    /// String here, and the operation must still be accepted. Only the object KIND is checked.
    /// </summary>
    [Fact]
    public void Validate_AcceptsANumberFormatOnAFieldWhoseDeclaredValueTypeIsNotNumeric()
    {
        var schema = Schema();
        schema.AvailableFields.Clear();
        schema.AvailableFields.Add(new FieldInfo
        {
            Name = "Name", FormulaForm = "{Customer.Name}", TableAlias = "Customer",
            ValueType = "String", HeadingText = "Name"
        });

        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetNumberFormat, Target = "CustomerName", DecimalPlaces = 0
        });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    [Theory]
    [InlineData("Title")]         // Text
    [InlineData("CustomerName")]  // Field
    public void Validate_AcceptsSetCanGrowOnTextAndField(string target)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetCanGrow, Target = target, CanGrow = true
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    /// <summary>
    /// A Subreport clips its own contents at the container height unless the CONTAINER can grow.
    /// The original { Text, Field } allowlist refused this, which produced a real defect: FDP
    /// comment tables overflowed their sub-report frames and collided with the section below.
    /// <para>
    /// Targets an ALREADY-embedded sub-report by its placed name, not one added in this plan:
    /// Crystal renames a sub-report it places, so every operation except setSubreportLink is
    /// rejected on one added in the same plan. Growing a sub-report is therefore always a second
    /// plan, run after the report has been saved and re-read.
    /// </para>
    /// </summary>
    [Fact]
    public void Validate_AcceptsSetCanGrowOnAnEmbeddedSubreport()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetCanGrow, Target = "Subreport1", CanGrow = true
        });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithEmbeddedSubreport());

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    /// <summary>
    /// The other half of the rule above: a sub-report added earlier in the SAME plan cannot be
    /// grown, because the name the plan used is not the name Crystal gave the placed object.
    /// </summary>
    [Fact]
    public void Validate_RejectsSetCanGrowOnASubreportAddedInTheSamePlan()
    {
        var plan = PlanOf(
            new LayoutOperation
            {
                Action = LayoutActions.AddSubreport, Section = "Section1", NewName = "Detail",
                ReportPath = @"C:\reports\detail.rpt",
                LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 500
            },
            new LayoutOperation
            {
                Action = LayoutActions.SetCanGrow, Target = "Detail", CanGrow = true
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsSetCanGrowOnALine()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetCanGrow, Target = "HeaderRule", CanGrow = true
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        // Names the offending kind, so this cannot be satisfied by the generic
        // "requires canGrow" message the missing-flag branch produces.
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("is a Line");
    }

    // --- setBorder ------------------------------------------------------------------
    // The whole point of setBorder is that it has NO kind allowlist: ISCRBorder hangs off
    // ISCRReportObject itself, every object carries one, and this project has already shipped two
    // too-narrow allowlists (FontableKinds without FieldHeading; CanGrowKinds without Subreport,
    // which caused the growing-cell defect setBorder exists to fix). The Box and Subreport cases
    // below are the regression tests against a third.

    [Theory]
    [InlineData("none")]
    [InlineData("single")]
    [InlineData("double")]
    [InlineData("dashed")]
    [InlineData("dotted")]
    public void Validate_AcceptsSetBorderWithOneSideInEveryStyle(string style)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetBorder, Target = "CustomerName", Bottom = style
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsSetBorderWithAllFourSidesAndAColour()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetBorder, Target = "CustomerName",
            Left = "single", Right = "single", Top = "dotted", Bottom = "double",
            Color = "#1F2A37"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsSetBorderWithColourOnly()
    {
        // Recolouring an existing border without restating its four sides is a legitimate
        // operation, so "colour but no side" must pass the at-least-one rule.
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetBorder, Target = "CustomerName", Color = "#C0C0C0"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsSetBorderCaseInsensitively()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetBorder, Target = "CustomerName", Left = "SINGLE", Top = "Dotted"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsSetBorderOnABox()
    {
        var schema = Schema();
        schema.Sections[0].Objects.Add(new ObjectInfo
        {
            Name = "Frame", Kind = "Box", LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 300
        });

        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetBorder, Target = "Frame", Bottom = "single"
        });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsSetBorderOnAnEmbeddedSubreport()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetBorder, Target = "Subreport1",
            Left = "single", Right = "single", Top = "single", Bottom = "single"
        });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithEmbeddedSubreport());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_AcceptsSetBorderOnALine()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetBorder, Target = "HeaderRule", Bottom = "single"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsSetBorderWithNoSideAndNoColour()
    {
        // A silent no-op is worse than a plan error: the caller believes a border was drawn and
        // only finds out from the rendered PDF.
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetBorder, Target = "CustomerName" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("at least one");
    }

    [Fact]
    public void Validate_RejectsSetBorderOnAnUnknownStyleName()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetBorder, Target = "CustomerName", Bottom = "crLineStyleSingle"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        // Names the offending side, so a caller with four sides supplied knows which one is wrong.
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("bottom");
    }

    [Fact]
    public void Validate_RejectsSetBorderOnAMalformedColour()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetBorder, Target = "CustomerName", Bottom = "single", Color = "red"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("#RRGGBB");
    }

    [Fact]
    public void Validate_RejectsSetBorderWithoutATarget()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetBorder, Bottom = "single" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires \"target\"");
    }

    [Fact]
    public void Validate_RejectsSetBorderOnAnObjectThatDoesNotExist()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetBorder, Target = "NoSuchThing", Bottom = "single"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("does not exist");
    }

    [Fact]
    public void Validate_RejectsSetCanGrowOnABox()
    {
        var schema = Schema();
        schema.Sections[0].Objects.Add(new ObjectInfo
        {
            Name = "Frame", Kind = "Box", LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 300
        });

        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetCanGrow, Target = "Frame", CanGrow = true
        });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("is a Box");
    }

    [Fact]
    public void Validate_RejectsSetCanGrowWithoutTheCanGrowFlag()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetCanGrow, Target = "Title" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires \"canGrow\"");
    }

    [Fact]
    public void Validate_AcceptsSetSuppressOnAnObject()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSuppress, Target = "Title", Suppress = true
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    /// <summary>
    /// setSuppress accepts ANY object kind, unlike setCanGrow: EnableSuppress is on
    /// ISCRObjectFormat, which every report object carries, and hiding a line or a box is a
    /// perfectly ordinary thing to want.
    /// </summary>
    [Fact]
    public void Validate_AcceptsSetSuppressOnALine()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSuppress, Target = "HeaderRule", Suppress = true
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    [Fact]
    public void Validate_AcceptsSetSuppressOnASectionWithSuppressIfBlank()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSuppress, Section = "Section3", Suppress = false, SuppressIfBlank = true
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: Why(result));
    }

    [Fact]
    public void Validate_RejectsSetSuppressWithBothTargetAndSection()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSuppress, Target = "Title", Section = "Section3", Suppress = true
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("not both");
    }

    [Fact]
    public void Validate_RejectsSetSuppressWithNeitherTargetNorSection()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetSuppress, Suppress = true });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("exactly one");
    }

    [Fact]
    public void Validate_RejectsSuppressIfBlankAlongsideAnObjectTarget()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSuppress, Target = "Title", Suppress = true, SuppressIfBlank = true
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("suppressIfBlank");
    }

    [Fact]
    public void Validate_RejectsSetSuppressWithoutTheSuppressFlag()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.SetSuppress, Section = "Section3" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires \"suppress\"");
    }

    [Fact]
    public void Validate_RejectsSetSuppressOnAnUnknownObject()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSuppress, Target = "NoSuchObject", Suppress = true
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("does not exist");
    }

    [Fact]
    public void Validate_RejectsSetSuppressOnAnUnknownSection()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.SetSuppress, Section = "NoSuchSection", Suppress = true
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("does not exist");
    }

    // ---- addGroup / addSort ------------------------------------------------

    /// <summary>
    /// Schema() plus a group that already exists, shaped exactly as ReportReader emits one: the
    /// group's own sort is in Sorts (Crystal creates one per group -- measured), and the two
    /// sections it created are in Sections under the names Crystal assigned.
    /// </summary>
    private static ReportSchema SchemaWithAGroup()
    {
        var schema = Schema();
        schema.Groups.Add(new GroupInfo
        {
            FieldRef = "{Command.stage_name}", Direction = SortDirections.Ascending,
            HeaderSection = "stagenameHeaderSection1", FooterSection = "stagenameFooterSection1"
        });
        schema.Sorts.Add(new SortInfo { FieldRef = "{Command.stage_name}", Direction = SortDirections.Ascending });
        schema.Sections.Add(new SectionInfo { Name = "stagenameHeaderSection1", Kind = "GroupHeader", HeightTwips = 250 });
        schema.Sections.Add(new SectionInfo { Name = "stagenameFooterSection1", Kind = "GroupFooter", HeightTwips = 250 });
        return schema;
    }

    [Fact]
    public void Validate_AcceptsAddGroupOnAnAvailableField()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsAddGroupWithoutAFieldRef()
    {
        var plan = PlanOf(new LayoutOperation { Action = LayoutActions.AddGroup });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires \"fieldRef\"");
    }

    [Fact]
    public void Validate_RejectsAddGroupOnAFieldThatIsNotInTheDataSource()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddGroup, FieldRef = "{Command.not_a_field}"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("availableFields");
    }

    [Theory]
    [InlineData(SortDirections.Ascending)]
    [InlineData(SortDirections.Descending)]
    [InlineData("DESCENDING")]
    public void Validate_AcceptsAddSortInEitherDirection(string direction)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSort, FieldRef = "{Command.stage_name}", Direction = direction
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsAddSortWithoutADirection()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSort, FieldRef = "{Command.stage_name}"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires \"direction\"");
    }

    [Fact]
    public void Validate_RejectsAnUnknownSortDirection()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSort, FieldRef = "{Command.stage_name}", Direction = "sideways"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("not a supported direction");
    }

    /// <summary>
    /// topN is called out on its own because it is the plausible one: it is a real
    /// CrSortDirectionEnum member, so an agent that has seen the Crystal API could reasonably try
    /// it. It must be a plan error, not a COM failure -- and not a silent pass-through, since
    /// neither operation has any field that could carry the N a TopN sort needs.
    /// </summary>
    [Theory]
    [InlineData("topN")]
    [InlineData("bottomN")]
    [InlineData("topNPercentage")]
    public void Validate_RejectsTheTopNSortDirections(string direction)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSort, FieldRef = "{Command.stage_name}", Direction = direction
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("no field to express");
    }

    [Fact]
    public void Validate_RejectsAddGroupWithAnUnsupportedDirection()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}", Direction = "topN"
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("not a supported direction");
    }

    /// <summary>
    /// ReportReader CLEARS AvailableFields whenever the report's data source cannot be enumerated,
    /// which is normal with no database connection. Both operations must still work then -- the
    /// trap removeTable's existence check and setNumberFormat's missing type check already
    /// sidestep. Note this is NOT addField's rule: addField binds new data and fails closed.
    /// </summary>
    [Fact]
    public void Validate_SkipsTheFieldCheckEntirelyWhenAvailableFieldsIsEmpty()
    {
        var schema = Schema();
        schema.AvailableFields.Clear();

        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Whatever.field}" },
            new LayoutOperation { Action = LayoutActions.AddSort, FieldRef = "{Whatever.other}", Direction = SortDirections.Descending });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Validate_RejectsAnOutOfRangeGroupIndex(int groupIndex)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}", GroupIndex = groupIndex
        });

        // Schema() has no groups, so 0 is the only accepted index.
        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("groupIndex");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Validate_RejectsAnOutOfRangeSortIndex(int sortIndex)
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSort, FieldRef = "{Command.stage_name}",
            Direction = SortDirections.Ascending, SortIndex = sortIndex
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("sortIndex");
    }

    [Fact]
    public void Validate_AcceptsAGroupIndexEqualToTheGroupCountBecauseThatAppends()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}", GroupIndex = 0
        });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// The cumulative-simulation test the brief calls for: the second addGroup must be judged
    /// against a report that already has ONE group, so groupIndex 1 is legal for it while it was
    /// illegal for the first. A validator that re-read the schema per operation would reject this.
    /// </summary>
    [Fact]
    public void Validate_IndexesASecondAddGroupAgainstTheFirstOne()
    {
        var schema = Schema();
        schema.AvailableFields.Add(new FieldInfo
        {
            Name = "stage_status", FormulaForm = "{Command.stage_status}",
            TableAlias = "Command", ValueType = "String", HeadingText = "Stage Status"
        });

        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}", GroupIndex = 0 },
            new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_status}", GroupIndex = 1 });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    [Fact]
    public void Validate_RejectsASecondGroupOnAFieldTheReportAlreadyGroupsOn()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}"
        });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithAGroup());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("already grouped");
    }

    [Fact]
    public void Validate_RejectsASecondGroupOnAFieldGroupedEarlierInTheSamePlan()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}" },
            new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}" });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("already grouped");
    }

    /// <summary>
    /// A grouped field is already sorted, because Crystal creates the group's sort itself
    /// (measured). Without this rule the plan would validate and then fail at COM with "The
    /// sorting already exists", mid-plan, faulting the session.
    /// </summary>
    [Fact]
    public void Validate_RejectsAddSortOnAFieldAGroupEarlierInThePlanAlreadySorts()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}" },
            new LayoutOperation
            {
                Action = LayoutActions.AddSort, FieldRef = "{Command.stage_name}",
                Direction = SortDirections.Descending
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("already sorted");
    }

    [Fact]
    public void Validate_RejectsAddSortOnAFieldTheReportAlreadySorts()
    {
        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddSort, FieldRef = "{Command.stage_name}",
            Direction = SortDirections.Descending
        });

        var result = LayoutPlanValidator.Validate(plan, SchemaWithAGroup());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("already sorted");
    }

    /// <summary>
    /// The good half of the in-plan section rule, and the reason the naming was measured rather
    /// than guessed: because Crystal's naming is deterministic, an addGroup and a placement into
    /// the section it creates validate as ONE plan.
    /// </summary>
    [Fact]
    public void Validate_AcceptsPlacingIntoAGroupHeaderCreatedEarlierInTheSamePlan()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}" },
            new LayoutOperation
            {
                Action = LayoutActions.AddText, Section = "stagenameHeaderSection1", NewName = "GroupTitle",
                Text = "Stage", LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 200
            },
            new LayoutOperation
            {
                Action = LayoutActions.AddText, Section = "stagenameFooterSection1", NewName = "GroupTotalLabel",
                Text = "Total", LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 200
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// The other half: a section that does NOT exist and is not created by this plan is still
    /// rejected, so the rule above is a prediction of a real name rather than a hole that accepts
    /// any unknown section name after an addGroup.
    /// </summary>
    [Fact]
    public void Validate_StillRejectsAnUnrelatedUnknownSectionAfterAnAddGroup()
    {
        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}" },
            new LayoutOperation
            {
                Action = LayoutActions.AddText, Section = "stage_nameHeaderSection1", NewName = "GroupTitle",
                Text = "Stage", LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 200
            });

        var result = LayoutPlanValidator.Validate(plan, Schema());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("does not exist");
    }

    /// <summary>
    /// The new group sections are registered 250 twips tall (measured), so an object taller than
    /// that is clipped -- and a resizeSection in the same plan is the documented way to make room.
    /// </summary>
    [Fact]
    public void Validate_LetsAResizeSectionInTheSamePlanMakeRoomInANewGroupHeader()
    {
        var tooTall = new LayoutOperation
        {
            Action = LayoutActions.AddText, Section = "stagenameHeaderSection1", NewName = "GroupTitle",
            Text = "Stage", LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 900
        };

        var withoutRoom = LayoutPlanValidator.Validate(
            PlanOf(new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}" }, tooTall),
            Schema());

        withoutRoom.IsValid.Should().BeFalse();
        withoutRoom.Errors.Should().ContainSingle().Which.Message.Should().Contain("past the height of section");

        var withRoom = LayoutPlanValidator.Validate(
            PlanOf(
                new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}" },
                new LayoutOperation { Action = LayoutActions.ResizeSection, Section = "stagenameHeaderSection1", HeightTwips = 1000 },
                tooTall),
            Schema());

        withRoom.IsValid.Should().BeTrue(because: string.Join("; ", withRoom.Errors.ConvertAll(e => e.Message)));
    }

    /// <summary>
    /// The collision guard. If the predicted name is already a real section, the plan cannot say
    /// which section it means, and writing into the wrong one silently is the one outcome worth
    /// refusing outright.
    /// </summary>
    [Fact]
    public void Validate_RejectsPlacingIntoAPredictedGroupSectionNameThatAlreadyExists()
    {
        var schema = Schema();
        schema.Sections.Add(new SectionInfo { Name = "stagenameHeaderSection1", Kind = "GroupHeader", HeightTwips = 250 });

        var plan = PlanOf(
            new LayoutOperation { Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_name}" },
            new LayoutOperation
            {
                Action = LayoutActions.AddText, Section = "stagenameHeaderSection1", NewName = "GroupTitle",
                Text = "Stage", LeftTwips = 0, TopTwips = 0, WidthTwips = 3000, HeightTwips = 200
            });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("ambiguous");
    }

    [Fact]
    public void Validate_AcceptsAddGroupAgainstAReportThatAlreadyGroupsOnADifferentField()
    {
        var schema = SchemaWithAGroup();
        schema.AvailableFields.Add(new FieldInfo
        {
            Name = "stage_status", FormulaForm = "{Command.stage_status}",
            TableAlias = "Command", ValueType = "String", HeadingText = "Stage Status"
        });

        var plan = PlanOf(new LayoutOperation
        {
            Action = LayoutActions.AddGroup, FieldRef = "{Command.stage_status}", GroupIndex = 1,
            Direction = SortDirections.Descending
        });

        var result = LayoutPlanValidator.Validate(plan, schema);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.ConvertAll(e => e.Message)));
    }
}

public class GroupSectionNamingTests
{
    /// <summary>
    /// The measured rule, pinned. "overall_comment" becoming "overallcomment" is the whole reason
    /// this could not be guessed: the obvious prediction keeps the underscore, and a validator
    /// registering "overall_commentHeaderSection1" would have validated plans that then threw
    /// mid-apply.
    /// </summary>
    [Theory]
    [InlineData("{Command.CardCode}", "CardCodeHeaderSection1", "CardCodeFooterSection1")]
    [InlineData("{sp_perf_ind_perf_overview;1.overall_comment}", "overallcommentHeaderSection1", "overallcommentFooterSection1")]
    [InlineData("{sp_x;1.emp_number}", "empnumberHeaderSection1", "empnumberFooterSection1")]
    public void Predicts(string fieldRef, string header, string footer)
    {
        GroupSectionNaming.HeaderSection(fieldRef).Should().Be(header);
        GroupSectionNaming.FooterSection(fieldRef).Should().Be(footer);
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
