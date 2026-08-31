using ModelContextProtocol.Server;

namespace VibeyReports.Mcp;

/// <summary>
/// Crystal report tools exposed over MCP: read_report, apply_layout, preview_report.
/// Intentionally empty in Task 9 (host + worker client only) so
/// AddMcpServer(...).WithTools&lt;ReportTools&gt;() compiles and the server starts.
/// Task 10 adds the tool methods.
/// </summary>
[McpServerToolType]
public sealed class ReportTools
{
}
