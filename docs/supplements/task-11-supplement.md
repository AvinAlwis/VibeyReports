# Task 11 supplement — scope boundary and corrections

**Where this file and the brief disagree, this file wins.**

---

## S1 (Scope) — do NOT modify the user's Claude Code configuration

The brief's Step 5 edits `C:\Users\avin.a\.claude.json` to register the MCP server, and Step 6 asks
for a Claude Code restart.

**Do neither.** That file is outside this repository and changes the user's global tooling. The
coordinator will present that change to the user for an explicit decision.

Instead:

- **Write the exact JSON snippet and the exact registration script into `README.md`**, ready for the
  user to apply, including the backup step. The brief already contains both — reproduce them
  faithfully.
- Say clearly in your report that registration was deliberately not performed and is pending the
  user's decision.

Steps 6 and 7 of the brief (restarting Claude Code, and **opening the generated `.rpt` in Crystal
Reports XI R2 by hand**) are human-only actions. Do not attempt them, do not simulate them, and do
not mark them done. Leave the README's MVP-verification checklist **unticked**, with a note that the
manual Crystal open is the real acceptance gate.

---

## S2 (Critical) — publish the worker to `dist\worker\`, not `dist\`

The brief publishes both projects into the same `dist` folder. **Do not.** Both carry
`VibeyReports.Contracts.dll`, and the net48 worker's dependency set can overwrite files the net10
MCP server needs. Whichever publishes second wins, and the failure is confusing.

Publish to **separate** directories:

```
dist\VibeyReports.Mcp.exe            <- net10.0 MCP server
dist\worker\VibeyReports.CrystalWorker.exe   <- net48 x86 worker
```

`WorkerLocator` already probes `AppContext.BaseDirectory/worker/` for exactly this reason (Task 9,
finding C4), and that probe is now covered by an isolated test. So the layout above works with no
environment variable — but still set `VIBEY_WORKER_PATH` explicitly in the README's registration
snippet as belt and braces, pointing at `D:\VibeyReports\dist\worker\VibeyReports.CrystalWorker.exe`.

---

## S3 — `dotnet publish -r win-x86` may not work for the net48 worker

The brief runs `dotnet publish ... -r win-x86 --self-contained false`. .NET Framework projects do
not publish the same way as SDK-style .NET projects, and this may fail or behave oddly.

If it does, fall back to a plain `dotnet publish -c Release -o <dir>` (or `dotnet build` + copy the
output). The worker's csproj already sets `<PlatformTarget>x86</PlatformTarget>`, so the produced
binary is 32-bit regardless of the RID. **Verify the result is genuinely x86** — the whole product
depends on it, because the Business Objects COM servers are 32-bit only. Say in your report which
command actually worked.

---

## S4 — the brief's test counts are stale

It says "35 tests total". The real totals at the start of this task are:

```
VibeyReports.Contracts.Tests    44
VibeyReports.CrystalWorker.Tests 55
VibeyReports.Mcp.Tests           22
```

Verify **"0 failed"** across all three suites, never a count match.

---

## S5 — verify the published artefacts actually work

This is the part of Task 11 that carries real value, so do it properly:

1. Both executables exist at the paths in S2, with non-zero size.
2. The **published worker** answers the protocol directly:
   ```bash
   echo '{"command":"read","reportPath":"D:/VibeyReports/tests/fixtures/SampleReport.rpt"}' | ./dist/worker/VibeyReports.CrystalWorker.exe
   ```
   Expect JSON beginning `{ "ok": true, "schema": ...`. Confirm it starts with `{` — no BOM.
3. The **published MCP server** starts and waits on stdin rather than exiting immediately. Note that
   it locates the worker via the `worker/` probe: if it cannot find it, it writes to **stderr** and
   returns exit code 1, so a clean start is itself evidence the probe worked against the real publish
   layout. Say in your report which you observed.
4. The full test suite passes (S4).

---

## Unchanged and still binding

- Never create a git remote and never push. Local commits only.
- `VibeyReports.Mcp` must never reference a Crystal assembly.
- **`CrystalReports.*` 13.x NuGet packages must not appear anywhere** — they save `.rpt` files in a
  format Crystal XI R2 and the customer's HR software cannot open.
- `dist/` is already in `.gitignore`; published binaries must not be committed.
