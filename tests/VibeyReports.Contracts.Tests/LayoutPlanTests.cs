using System.Text.Json;
using FluentAssertions;
using VibeyReports.Contracts;
using Xunit;

namespace VibeyReports.Contracts.Tests;

public class LayoutPlanTests
{
    [Fact]
    public void LayoutPlan_DeserialisesTheSpecExampleOperation()
    {
        const string json = """
        {
          "planVersion": 1,
          "operations": [
            { "action": "move", "target": "CustomerName", "leftTwips": 400, "topTwips": 50 }
          ]
        }
        """;

        var plan = JsonSerializer.Deserialize<LayoutPlan>(json, VibeyJson.Options)!;

        plan.PlanVersion.Should().Be(1);
        plan.Operations.Should().HaveCount(1);
        plan.Operations[0].Action.Should().Be(LayoutActions.Move);
        plan.Operations[0].Target.Should().Be("CustomerName");
        plan.Operations[0].LeftTwips.Should().Be(400);
        plan.Operations[0].TopTwips.Should().Be(50);
        plan.Operations[0].WidthTwips.Should().BeNull();
    }

    [Fact]
    public void LayoutPlan_DeserialisesAllTenMvpActions()
    {
        const string json = """
        {
          "planVersion": 1,
          "operations": [
            { "action": "move",          "target": "A", "leftTwips": 1, "topTwips": 2 },
            { "action": "resize",        "target": "A", "widthTwips": 3, "heightTwips": 4 },
            { "action": "setFont",       "target": "A", "fontName": "Calibri" },
            { "action": "setFontSize",   "target": "A", "fontSizePt": 11.5 },
            { "action": "setBold",       "target": "A", "bold": true },
            { "action": "setAlignment",  "target": "A", "alignment": "Centre" },
            { "action": "addText",       "section": "Section1", "newName": "Title", "text": "Payroll",
              "leftTwips": 0, "topTwips": 0, "widthTwips": 5000, "heightTwips": 320 },
            { "action": "addLine",       "section": "Section1", "newName": "Rule",
              "leftTwips": 0, "topTwips": 340, "widthTwips": 5000, "heightTwips": 0 },
            { "action": "addBox",        "section": "Section1", "newName": "Frame",
              "leftTwips": 0, "topTwips": 0, "widthTwips": 5000, "heightTwips": 400 },
            { "action": "resizeSection", "section": "Section3", "heightTwips": 600 }
          ]
        }
        """;

        var plan = JsonSerializer.Deserialize<LayoutPlan>(json, VibeyJson.Options)!;

        plan.Operations.Should().HaveCount(10);
        plan.Operations[3].FontSizePt.Should().Be(11.5f);
        plan.Operations[4].Bold.Should().BeTrue();
        plan.Operations[6].Text.Should().Be("Payroll");
        plan.Operations[9].Section.Should().Be("Section3");
    }

    [Fact]
    // Deliberately NOT named after the number of actions. The previous name hardcoded "Sixteen"
    // and rotted silently when addSubreport/setSubreportLink took it to eighteen: the name stopped
    // describing the test long before anyone noticed the test itself was red.
    public void LayoutActions_All_ContainsEverySupportedActionAndNothingElse()
    {
        LayoutActions.All.Should().BeEquivalentTo(new[]
        {
            "move", "resize", "setFont", "setFontSize", "setBold",
            "setAlignment", "addText", "addLine", "addBox", "resizeSection", "addField", "removeObject",
            "setTextColor", "setFillColor", "setLineColor", "setSectionBackground",
            "addSubreport", "setSubreportLink",
            "removeTable", "addTable", "setTableLocation"
        });
        LayoutActions.All.Should().HaveCount(21);
    }

    [Fact]
    public void LayoutOperation_DeserialisesTableName()
    {
        const string json = """
        { "action": "addTable", "target": "sp_a;1", "tableName": "sp_b;1", "newName": "sp_b;1" }
        """;

        var op = JsonSerializer.Deserialize<LayoutOperation>(json, VibeyJson.Options)!;

        op.Action.Should().Be(LayoutActions.AddTable);
        op.Target.Should().Be("sp_a;1");
        op.TableName.Should().Be("sp_b;1");
        op.NewName.Should().Be("sp_b;1");
    }

    [Fact]
    public void LayoutOperation_DeserialisesColor()
    {
        const string json = """
        { "action": "setTextColor", "target": "CustomerName", "color": "#1F2A37" }
        """;

        var op = JsonSerializer.Deserialize<LayoutOperation>(json, VibeyJson.Options)!;

        op.Action.Should().Be(LayoutActions.SetTextColor);
        op.Color.Should().Be("#1F2A37");
    }
}
