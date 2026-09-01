using System.Text.Json;
using FluentAssertions;
using VibeyReports.Contracts;
using Xunit;

namespace VibeyReports.Contracts.Tests;

public class WorkerProtocolTests
{
    [Fact]
    public void WorkerRequest_RoundTripsAnApplyCommandWithAPlan()
    {
        var request = new WorkerRequest
        {
            Command = WorkerCommands.Apply,
            ReportPath = @"C:\in.rpt",
            OutputPath = @"C:\out.rpt",
            Overwrite = true,
            Plan = new LayoutPlan
            {
                Operations = { new LayoutOperation { Action = LayoutActions.Move, Target = "A", LeftTwips = 1, TopTwips = 2 } }
            }
        };

        var json = JsonSerializer.Serialize(request, VibeyJson.Options);
        var back = JsonSerializer.Deserialize<WorkerRequest>(json, VibeyJson.Options)!;

        back.Command.Should().Be("apply");
        back.Overwrite.Should().BeTrue();
        back.Plan!.Operations.Should().ContainSingle();
    }

    [Fact]
    public void WorkerResponse_Failure_CarriesTheErrorAndValidationDetail()
    {
        var response = WorkerResponse.Failure("bad plan", new ValidationResult
        {
            IsValid = false,
            Errors = { new ValidationError { OperationIndex = 0, Message = "nope" } }
        });

        var json = JsonSerializer.Serialize(response, VibeyJson.Options);
        var back = JsonSerializer.Deserialize<WorkerResponse>(json, VibeyJson.Options)!;

        back.Ok.Should().BeFalse();
        back.Error.Should().Be("bad plan");
        back.ValidationErrors.Should().ContainSingle().Which.Message.Should().Be("nope");
    }

    [Fact]
    public void WorkerCommands_ContainsTheThreeSupportedCommands()
    {
        WorkerCommands.All.Should().BeEquivalentTo(new[] { "read", "apply", "render" });
    }
}
