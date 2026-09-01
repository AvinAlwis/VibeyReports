using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeyReports.Mcp;

string workerPath;
try
{
    workerPath = WorkerLocator.Find();
}
catch (FileNotFoundException ex)
{
    await Console.Error.WriteLineAsync($"[MCP Server] Startup error: {ex.Message}");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
// All log output must go to stderr — stdout is reserved for MCP JSON-RPC messages
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(_ => new CrystalWorkerClient(workerPath));

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "vibey-reports", Version = "1.0.0" };
    })
    .WithStdioServerTransport()
    .WithTools<ReportTools>();

await builder.Build().RunAsync();
return 0;
