# Task 9 supplement — corrections from a working MCP server on this machine

There is a **working, installed ModelContextProtocol 1.2.0 server** on this machine:
`D:\PHR-X-DB-MCP-SERVER\src\PeoplesHR.DBMCPServer\`. I read its `Program.cs` and `Tools/SchemaTools.cs`
and verified the SDK's type surface directly. **Where this file and the brief disagree, this file wins.**

---

## C1 (Critical) — `builder.Logging.ClearProviders()` is mandatory

The brief writes:

```csharp
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
```

That is **not sufficient**. `Host.CreateApplicationBuilder` already registers default logging
providers — including a Console provider that writes to **stdout**. Setting
`LogToStandardErrorThreshold` on the provider *you* add does nothing about the one already there.

MCP speaks JSON-RPC over stdout. **Any log line written to stdout corrupts the protocol stream**, and
the failure mode is ugly: the client sees malformed JSON-RPC and the server looks broken or simply
never connects.

The working server on this machine does it correctly:

```csharp
builder.Logging.ClearProviders();
// All log output must go to stderr — stdout is reserved for MCP JSON-RPC messages
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
```

Copy that shape exactly, comment included.

---

## C2 — use `WithTools<T>()`, not `WithToolsFromAssembly()`

The brief uses `.WithToolsFromAssembly()`. The proven pattern here is explicit registration:

```csharp
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "vibey-reports", Version = "1.0.0" };
    })
    .WithStdioServerTransport()
    .WithTools<ReportTools>();
```

Explicit is better here: assembly scanning silently registers nothing if the attribute is missing or
the type is not public, and you would not find out until the tools failed to appear in a client.
Also note `ServerInfo` — the brief omits it, and without it the server reports a default name.

`ReportTools` does not exist until Task 10. For Task 9, either register nothing and add
`.WithTools<ReportTools>()` in Task 10, or create the class with no tool methods yet. Say which you did.

---

## C3 — `Program.cs` returns an exit code

The working server's top-level program returns `int` — `1` on a startup/configuration failure (after
writing the reason to **stderr**), `0` on clean shutdown. Do the same. Locating the worker is the
obvious startup failure here: if `WorkerLocator.Find()` throws, write the message to stderr and
return `1` rather than letting an unhandled exception escape.

---

## C4 — `WorkerLocator` must also probe a `worker/` subdirectory

Carried from my pre-flight scan. Task 11 publishes the net10.0 MCP server and the net48 x86 worker to
**separate** directories, because publishing both into one folder lets the worker's .NET Framework
dependency set overwrite files the net10 app needs — they both carry `VibeyReports.Contracts.dll`.

The layout will be `dist/VibeyReports.Mcp.exe` and `dist/worker/VibeyReports.CrystalWorker.exe`.

So the probe order is:

1. `VIBEY_WORKER_PATH` environment variable, if set (fail loudly if it points at a missing file)
2. Next to the MCP assembly: `AppContext.BaseDirectory/VibeyReports.CrystalWorker.exe`
3. **`AppContext.BaseDirectory/worker/VibeyReports.CrystalWorker.exe`** ← add this
4. Walk up looking for the worker's build output (the brief's existing fallback, for dev runs)

---

## C5 — verified SDK type surface (for your reference and Task 10's)

I confirmed these exist in `ModelContextProtocol.Core` 1.2.0 by inspecting the assembly:

```
ContentBlock, TextContentBlock, ImageContentBlock, AudioContentBlock, CallToolResult
```

So Task 10's plan to return a rendered PNG as `ImageContentBlock` is sound. Tool-method conventions
from the working server:

```csharp
[McpServerToolType]
public sealed class ReportTools
{
    [McpServerTool(Name = "read_report", ReadOnly = true)]
    [Description("...")]
    public async Task<string> ReadReportAsync(
        [Description("...")] string reportPath,
        CancellationToken cancellationToken = default)
```

Note `ReadOnly = true` on tools that do not mutate anything (`read_report`, `preview_report`), a
trailing `CancellationToken cancellationToken = default`, and `[Description]` from
`System.ComponentModel` on both the method and each parameter.

---

## C6 — the worker is a child process, and its failures are data, not exceptions

The worker exits **0 whenever it produced a JSON response**, including `ok: false`. Non-zero means it
crashed before it could answer. So:

- Non-zero exit **or** empty stdout → build a `WorkerResponse.Failure(...)` that includes the exit
  code and a truncated stderr, so the cause is visible to Claude rather than lost.
- Valid JSON with `ok: false` → return it as-is. That is a normal, successful round trip.
- Do **not** throw for `ok: false`.

The worker's stdout is BOM-less UTF-8 (Task 8 asserts this with a raw-byte test), so
`JsonSerializer.Deserialize` on the captured string works directly.

---

## Unchanged and still binding

- `VibeyReports.Mcp` targets **net10.0**; the worker stays **net48 x86**. They talk over stdin/stdout
  JSON only — the MCP project must never reference a Crystal assembly.
- One worker process per request. It keeps legacy COM state from leaking between operations, and it is
  why a partially-applied document can never outlive a single call.
- `WorkerRequest` / `WorkerResponse` / `WorkerCommands` field names come from `VibeyReports.Contracts`
  and are shared with the worker. Do not redefine or rename them.
- Package versions match the house convention: `ModelContextProtocol` 1.2.0,
  `Microsoft.Extensions.Hosting` 10.0.7, `Microsoft.Extensions.Logging.Console` 10.0.7.
