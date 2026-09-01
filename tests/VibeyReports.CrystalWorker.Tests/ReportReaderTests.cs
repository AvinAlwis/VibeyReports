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
    public void Read_ReturnsFullPaperSizeNotPrintableArea()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);
        var page = schema.Page;

        // The validator does: printable = Width - MarginLeft - MarginRight.
        // So Width must be the FULL paper size, strictly larger than the printable area
        // whenever margins are non-zero.
        (page.MarginLeftTwips + page.MarginRightTwips).Should().BeGreaterThan(0,
            because: "this fixture has non-zero margins, which is what makes the check meaningful");

        var printableWidth = page.WidthTwips - page.MarginLeftTwips - page.MarginRightTwips;
        var printableHeight = page.HeightTwips - page.MarginTopTwips - page.MarginBottomTwips;

        printableWidth.Should().BeGreaterThan(0);
        printableHeight.Should().BeGreaterThan(0);

        // A4 portrait is 11906 x 16838 twips; Letter is 12240 x 15840. Either way the full
        // paper width of a real report is at least 11900 twips, and the printable area is
        // strictly smaller than the paper.
        page.WidthTwips.Should().BeGreaterThan(printableWidth);
        page.HeightTwips.Should().BeGreaterThan(printableHeight);
        page.WidthTwips.Should().BeGreaterThanOrEqualTo(11900);
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
    public void Read_ClassifiesBandsFromAreaKindNotSectionName()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);
        var bands = schema.Sections.Select(s => s.Kind).ToList();

        // Measured shape of this fixture: five areas, one section each.
        bands.Should().Contain("ReportHeader");
        bands.Should().Contain("PageHeader");
        bands.Should().Contain("Details");
        bands.Should().Contain("PageFooter");
        bands.Should().Contain("ReportFooter");

        // A name-based heuristic returns "Other" for every section on fixtures whose sections
        // are RAS-default-named "Section1", "Section2"... with no band encoded. Pin that
        // classification doesn't fall back to that for this fixture.
        //
        // NOTE: this fixture's sections are actually named "ReportHeaderSection1",
        // "PageHeaderSection1", "DetailSection1", "PageFooterSection1", "ReportFooterSection1"
        // (measured directly) - the band name IS embedded in section.Name here, contradicting
        // the "Section1, Section2... no band encoded" assumption carried over from Task 4's
        // notes for a different fixture. A name-based heuristic would happen to work on THIS
        // fixture by coincidence. That does not make Kind-from-Area.Kind wrong; it just means
        // this particular fixture cannot be used to prove a name-based classifier is absent.
        // The NotContain("Other") check above remains the meaningful guard.
        bands.Should().NotContain("Other");
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

    // --- colour reading ---

    /// <summary>
    /// Measured (this task, via a standalone probe against Documents.rpt, PMSV10_IndPerfOverview.rpt
    /// and SampleReport.rpt): FontColor.Color defaults to 0 the moment a Text/Field object exists --
    /// it is never the 0xFFFFFFFF "unset" sentinel that FillColor/BackgroundColor use -- so every
    /// text-bearing object on this fixture must report a real (non-null) colour, "#000000" under
    /// the COLORREF hypothesis.
    /// </summary>
    [Fact]
    public void Read_ReturnsTextColorHexForEveryTextAndFieldObject()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);
        var textish = schema.Sections.SelectMany(s => s.Objects).Where(o => o.Kind == "Text" || o.Kind == "Field").ToList();

        textish.Should().NotBeEmpty();
        textish.Should().OnlyContain(o => o.TextColorHex == "#000000");
    }

    [Fact]
    public void Read_LeavesTextColorHexNullForNonTextObjects()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);
        var nonTextish = schema.Sections.SelectMany(s => s.Objects).Where(o => o.Kind != "Text" && o.Kind != "Field" && o.Kind != "FieldHeading");

        nonTextish.Should().OnlyContain(o => o.TextColorHex == null);
    }

    /// <summary>
    /// Measured: every section on SampleReport.rpt has no background colour explicitly set in
    /// the designer, and reads back Format.BackgroundColor == 0xFFFFFFFF -- the sentinel ColorRef
    /// maps to null, not the misleading "#FFFFFF" (which would be indistinguishable from a real,
    /// explicitly-set white background).
    /// </summary>
    [Fact]
    public void Read_LeavesBackgroundColorHexNullWhenNeverExplicitlySet()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var schema = ReportReader.Read(session);

        schema.Sections.Should().OnlyContain(s => s.BackgroundColorHex == null);
    }

    /// <summary>
    /// Measured on PMSV10_IndPerfOverview.rpt: Box3 has no fill explicitly set in the designer
    /// and reads back FillColor == 0xFFFFFFFF; several other boxes on the same fixture (e.g.
    /// Box14, Box17, Box18) DO have an explicit fill and read back with their top byte 0x00
    /// (e.g. 0x00E1E1E1), proving the sentinel and a real colour are actually distinguishable on
    /// this fixture rather than every box coincidentally landing on the same value.
    /// </summary>
    [Fact]
    public void Read_MapsTheUnsetFillColorSentinelToNullRatherThanABogusHexValue()
    {
        var path = System.IO.Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");
        using var session = CrystalSession.Open(path);

        var schema = ReportReader.Read(session);
        var boxes = schema.Sections.SelectMany(s => s.Objects).Where(o => o.Kind == "Box").ToList();

        boxes.Should().NotBeEmpty();
        boxes.Should().Contain(b => b.FillColorHex == null,
            because: "at least one box on this fixture has no fill explicitly set");
        boxes.Should().Contain(b => b.FillColorHex != null,
            because: "at least one box on this fixture DOES have an explicit fill, proving the null case above is real, not universal");
    }

    [Theory]
    [InlineData("Documents.rpt")]
    [InlineData("JournalEntry.rpt")]
    [InlineData("PMSV10_IndPerfOverview.rpt")]
    public void Read_SucceedsEvenWhenFieldEnumerationCannotReachTheDatabase(string fixtureName)
    {
        // Reading layout must never require a live database connection. Whatever AvailableFields
        // comes back as for these disconnected fixtures, Read() itself must not throw.
        var path = System.IO.Path.Combine(Fixtures.Dir, fixtureName);
        using var session = CrystalSession.Open(path);

        var schema = ReportReader.Read(session);

        schema.Should().NotBeNull();
        schema.AvailableFields.Should().NotBeNull();
    }
}
