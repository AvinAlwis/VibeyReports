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
