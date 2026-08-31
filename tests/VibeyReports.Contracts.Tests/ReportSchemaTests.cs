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
