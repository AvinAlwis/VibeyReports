# Decision log — Vibey Reports

This is the verbatim coordination ledger from the subagent-driven build of this project
(2026-08-31 / 2026-09-01). It is preserved here because it is the only record of **why** the
code diverges from `docs/superpowers/plans/2026-08-31-vibey-reports.md` in the places it does.

It contains **77 rulings** — decisions taken on the user's behalf during execution, each recorded
with its reasoning and what it would cost if wrong. Search for `Ruling:` to find them.

Nine defects in the original plan were found by measurement rather than by reading; those are
summarised separately in [sdk-notes.md](sdk-notes.md), which is authoritative about the Crystal SDK
wherever it disagrees with the plan.

Two entries record **my own errors as coordinator** (a `git add -A` that swept in a live
implementer's work, and a ledger write that silently failed because of a stale working directory).
They are kept deliberately — an accurate record is worth more than a flattering one.

---

# SDD ledger — plan: D:/VibeyReports/docs/superpowers/plans/2026-08-31-vibey-reports.md

Spec: docs/superpowers/specs/2026-08-31-vibey-reports-spec.md (read, reachable)
Branch: feat/vibey-reports  Repo root: D:/VibeyReports  Base commit: 061e467

## Pre-flight scan

### Cross-task pairs (shared file or interface)

| Pair | Producer -> Consumer | Finding |
|---|---|---|
| T1 -> T3 | ReportSchema/PageInfo/SectionInfo/ObjectInfo -> Validate(plan, schema) | OK |
| T1 -> T5 | ReportSchema shape -> ReportReader.Read output | **CONFLICT G** (see rulings) |
| T1 -> all | VibeyJson.Options | OK |
| T2 -> T3 | LayoutPlan/LayoutOperation/LayoutActions -> validator | OK |
| T2 -> T6 | same -> LayoutApplier switch arms | OK, all 10 actions covered |
| T3 -> T6 | LayoutPlanValidator.Validate(plan, schema) | OK, signature matches |
| T4 -> T5,6,7,8 | CrystalSession.Open/Document/SaveAs | OK |
| T5 -> T6 | ReportReader.Read(session) | OK |
| T5 -> T8 | ReportReader.Read(session) | OK |
| T6 -> T8 | LayoutApplier.Apply + InvalidPlanException.Result | OK |
| T7 -> T8 | ReportRenderer.ExportPdf(session) | OK |
| T8 -> T9 | WorkerRequest/WorkerResponse/WorkerCommands | OK |
| T9 -> T10 | CrystalWorkerClient.Read/Apply/RenderAsync | OK |
| T9 -> T11 | WorkerLocator discovery vs publish layout | **CONFLICT Q** (see rulings) |
| T7 -> T10 | ExportPdf must work to make minimal.pdf fixture | **CONFLICT S** (see rulings) |
| T8/T9 | both create a Program.cs, different projects | OK, no collision |

### Per-task self-consistency

| Task | Tests vs code vs files | Finding |
|---|---|---|
| T1 | test file written (Step 5) before project created (Step 6) | **CONFLICT W** (ordering) |
| T2 | 3 tests vs LayoutPlan.cs | OK |
| T3 | 14 tests vs validator; uses C#9 patterns on netstandard2.0 | OK (LangVersion=latest in Directory.Build.props) |
| T4 | same test-before-project ordering as T1 | CONFLICT W |
| T5 | tests do not catch the page-width semantics risk | CONFLICT G |
| T6 | 9 tests vs 10 switch arms; ResizeSection predicate loose | OK (deferred minor) |
| T7 | 2 tests; DB-logon risk already documented in-task | OK |
| T8 | states "20 total" worker tests | **CONFLICT N1** (count wrong: 29) |
| T9 | same test-before-project ordering | CONFLICT W |
| T10 | node fallback for fixture gen; ReportTools DI construction | CONFLICT R, note O |
| T11 | states "35 tests total" | **CONFLICT N2** (count wrong: 62) |

## Rulings (pre-flight)

Ruling W: Implementers MAY scaffold the csproj before writing the test file. TDD requires the test be written and OBSERVED FAILING before the implementation code — not before the project file. Cost if wrong: none; ordering is presentational.

Ruling G: PageInfo.WidthTwips/HeightTwips MUST be full paper size, NOT content size. T3's validator computes printable = width - margins, so if T5 returns an already-content-reduced width the margins are double-subtracted and valid layouts get rejected. T5 must empirically confirm what PrintOptions.PageContentWidth means and add margins back if it is content-only. T5 must also add a test asserting WidthTwips > MarginLeft + MarginRight + 1440. Cost if wrong: validator spuriously rejects legitimate plans; caught by the new test.

Ruling Q: Do NOT publish the net10.0 MCP server and the net48 worker into the same directory — both carry VibeyReports.Contracts.dll and the worker's net48 dependency set can overwrite the net10 app's. T11 publishes worker to dist/worker/. T9's WorkerLocator gains a probe for Path.Combine(BaseDirectory, "worker", ExeName) after the side-by-side probe. T11's MCP registration also sets VIBEY_WORKER_PATH explicitly. Cost if wrong: extra harmless probe path.

Ruling S: T10's minimal.pdf fixture need only be a valid one-page PDF. If T7's Crystal render is blocked (DB logon), generate it by any other means. The rasterizer test must not depend on Crystal. Cost if wrong: none.

Ruling R: T10 Step 3 uses the PowerShell branch only; drop the node -e fallback (node may be absent). Cost if wrong: none.

Ruling N1/N2: Stated aggregate test counts are WRONG and are advisory only. Correct: Contracts 22, Worker 29, Mcp 11, total 62. Implementers and reviewers verify "0 failed", never a count match. Cost if wrong: none; counts are not requirements.

Note O: ReportTools has a ctor dependency on CrystalWorkerClient; T9 registers that in DI, and ModelContextProtocol activates tool types from DI. If WithToolsFromAssembly cannot construct it, register ReportTools explicitly.

Deferred minor (known, do not loop on): ValidationResult.Ok() is unused by any task.

## Progress

CONSTRAINT (user, mid-run): Local branches only. Never create a remote, never push.
  Verified: no remote configured. `git remote -v` is empty. Finishing must stay local.
Task 1: complete (commits 061e467..5d2dde5, review clean — spec OK, quality approved)
  Ruling: accepted `.slnx` over `.sln` (SDK 10 default; `dotnet sln add` + `dotnet test` both handle it). Cost if wrong: cosmetic only.
  Ruling: accepted repo-scoped NuGet.Config with <clear/> + nuget.org. Necessary — the machine-wide config carries a private PeoplesHR Azure DevOps feed that 401s and blocks ALL restores. Cost if wrong: none; it only narrows sources for this repo.
  Resolved reviewer ⚠️ myself: all 4 fixture .rpt files verified sha256-identical to their sources.
  Reviewer Minor "no .gitignore for bin/obj" = FALSE POSITIVE; .gitignore landed in 061e467, before the diff range. Verified `git check-ignore` covers bin/.
Task 2: complete (commits 5d2dde5..09ce634, review clean — spec OK, quality approved)
  Resolved reviewer warning myself: Contracts csproj still netstandard2.0, untouched by this task.
Task 2: minor (deferred): LayoutActions_All test uses BeEquivalentTo (set equality), so a reordering of LayoutActions.All would not fail it. Order is NOT functionally load-bearing (All is used for a Contains check and error text only) — my constraint block overstated it. Not worth a loop.
Task 3: review round 1 — spec OK, quality NEEDS WORK. 1 Critical + 5 Important + test gaps.
  Reviewer (opus) verified the code byte-identical to the brief, then found bugs in MY brief's design.
  Ruling: F1 (Critical) FIX — SimObject drops ObjectInfo.Kind, so setFont/setBold on a Line/Box/Picture
    validates clean then throws in Task 6's applier, leaving a half-applied document. Breaks the exact
    contract this task exists to uphold. Cost if wrong: none, gate is strictly narrowing.
  Ruling: F2 (Important) FIX — int overflow wraps left+width negative, so leftTwips 2147483000 is ACCEPTED.
    Compare as long. Cost if wrong: none.
  Ruling: F3 (Important) FIX — resize uses w<=0 but addLine allows w=0, so a vertical rule can be created
    but never resized. Relax resize to w<0 for consistency. Cost if wrong: an AI could resize something to
    zero width and make it invisible; acceptable, it is a layout choice and reversible.
  Ruling: F4 (Important) FIX with "don't make it worse" semantics — DESIGN CHANGE away from plan text.
    move requires both coords, so a vertical-only move restates left and re-trips the width check. Real
    legacy Crystal reports routinely overflow the printable margin, so the plan as written locks the AI
    out of move on the product's PRIMARY input. New rule: accept if the resulting right edge is within
    printable width OR no worse than before. Hard check kept for resize and adds. Spec intent ("redesign
    existing reports") binds over the plan's stricter text. Cost if wrong: already-overflowing objects can
    be moved vertically without being brought inside the margin — visible in preview, correctable.
  Ruling: F5 (Important) FIX — NRE on operations:null / [null]. A gate on model-generated JSON must return
    IsValid=false, never throw. Cost if wrong: none.
  Ruling: F6 (Important) FIX — no default: arm; an action added to All without a case would pass with ZERO
    checks. Cost if wrong: none, unreachable today by design.
  Ruling: F7 (Minor, fixing anyway) FIX — ToDictionary throws on duplicate section names; a crash on a real
    report is worse than a validation error. Duplicate-OBJECT-name divergence deferred: RAS guarantees
    unique names and Task 5 tests assert it.
  Ruling: test gaps T1-T9 FIX — reviewer demonstrated 12 mutations that pass all 14 existing tests, and BOTH
    behaviours the brief called load-bearing (mutation-on-failure, shrink-clip) have zero coverage. Scoped to
    the Critical/Important findings plus those two; did NOT take all 12. Cost if wrong: some minor branches
    stay unpinned until final review.
Task 3: minor (deferred): add-branch message completeness; IsNullOrEmpty vs IsNullOrWhiteSpace for op.Text;
  addText/addBox accept zero size; PlanVersion never validated; Suppressed sections ignored;
  ValidationResult.Ok() unused; dead ValidationResult alloc before arg guards; brittle substring assertions;
  PlanOf/With helper awkwardness.
CARRY TO TASK 6: reviewer flagged that Task 6's Move/Resize set Left/Top/Width/Height on the clone but NOT
  Right/Bottom, which AddLine/AddBox set explicitly. Moving or resizing a Line or Box will likely leave stale
  endpoint geometry. Must be addressed when Task 6 is implemented.
Task 3: fix round 1/5 (16 addressed, 0 open — F1-F7 + T1-T9 all verified; commits 0811b64..5bb8c15)
  Ruling: accepted implementer's resolution of a contradiction in MY findings file — F5 prose said "treat null
    Operations as empty list" but T9 required Operations=null to be INVALID. Resolved as null -> IsValid=false,
    empty non-null list -> valid no-op. T9 was the more specific requirement. Cost if wrong: a malformed plan
    reports an error instead of silently no-opping, which is the safer direction.
  Re-review confirmed: no new breakage, no existing test expectations changed, deferred list untouched,
    Contracts still netstandard2.0. Note: shared test fixture Schema() gained 2 objects (HeaderRule, Wide) —
    additive, no existing assertion perturbed.
Task 3: complete (commits 09ce634..5bb8c15, review clean after 1 fix round) — 32 tests, 0 failed
Task 3: minor (deferred, additional): schema.Page==null short-circuits before evaluating any operations;
  Move's negative-coord check lacks an early return so one bad move can emit 2 messages (pre-existing).

## Scope change — Addendum A: addField (user-approved 2026-08-31)

User approved adding a bound-database-field operation after showing a target layout (4-column table,
bold headings + stage_name/stage_period/stage_status/stage_outcome_score in Details). Plan amended and
committed as 4b1bcf1. NOT a ruling — explicit user decision.

Verified against installed 11.5 assemblies before writing the addendum:
  DatabaseController.Database.Tables -> ISCRTable.DataFields -> ISCRDBField
  ISCRDBField.FormulaForm gives "{Table.Field}" == exactly what ISCRFieldObject.DataSource needs
  and exactly what ObjectInfo.DataSource already reports for existing fields.

Ruling: addField does NOT weaken Global Constraint 5. It cannot add tables, change connections, alter
  SQL or touch table links — the validator rejects any fieldRef not already in the report's own data
  source. Only which existing fields get placed changes. Cost if wrong: a plan could place a field the
  report technically exposes but the user did not intend; visible in preview, and the .rpt is a new file.

Ruling: implement via direct FieldObjectClass construction + Add(obj, section, -1), NOT AddByName.
  AddByName picks its own section and position, so it cannot satisfy a positioned layout plan.
  AddByName documented as the fallback if DataSource binding proves not to persist. Cost if wrong:
  one extra fix round in Task 6b; the test asserts DataSource survives save+reopen either way.

Ruling: Task 5 scope WIDENED to also extract ReportSchema.AvailableFields. The spec's Phase 5 requires
  sending the AI "Available fields" and my original plan dropped it — which is the actual reason
  addField was unreachable. Restoring it is spec compliance, not scope creep. Cost if wrong: a little
  extra work in a task not yet dispatched.

Ruling: Task 6b sequenced AFTER Task 6, not merged into it. The core applier loop should be proven
  before adding the one operation that touches data binding. Cost if wrong: one extra review cycle.

Ruling: Task 6b is authorised to change ONE pre-existing test —
  LayoutActions_All_ContainsExactlyTheTenMvpActions — since All legitimately grows to eleven entries.
  Every other existing test must still pass unchanged.

Sequence is now: T4, T5 (amended), T6, T6b, T7, T8, T9, T10 (desc includes addField), T11.

Task 4: BLOCKED report received (commit ec7ab03) — 3 failed, 1 skipped. Implementer diagnosis was CORRECT
  on both counts; both were bugs in MY brief.
  B1 BLOCKER: `new ReportClientDocument()` activates the standalone RAS COM server, which TCP self-connects
    to port 1566 and hangs ~20-24s. Not fixable in code. Implementer correctly refused to touch
    firewall/hosts/services.
  B2: ISCRReportDefinition has NO Sections property. Confirmed by reflection: it has Areas, and ISCRArea
    carries Sections + Kind. This is EXACTLY the soft spot my own plan self-review flagged as "one known
    soft spot" for Task 5. Now confirmed as fact, not risk.

  Ruling: FIX B1 by opening through the classic engine — ReportDocument.Load(path) then .ReportClientDocument
    — instead of constructing ReportClientDocument directly. This is what D:\RptToXml does and I should have
    specified it. VERIFIED EMPIRICALLY by me in a 32-bit PowerShell harness against SampleReport.rpt:
      Load 5399ms; RCD bridge 2ms with NO network connect; 5 areas reachable.
    Then verified the FULL WRITE PATH in the same process:
      GetAllReportObjects -> Clone(true) -> mutate -> Modify(old,new) -> SaveAs(name,dir,0)
      -> reopen -> change PERSISTED (left=720) -> source file byte-length unchanged.
    Cost if wrong: none — this is measured, not reasoned. It also derisks Tasks 5, 6 and 7 in advance.

  Ruling: FIX B2 by walking ReportDefinition.Areas and summing area.Sections. Test intent unchanged.
    Observed on SampleReport.rpt: ReportHeaderArea1 kind=1, PageHeaderArea1 kind=2, DetailArea1 kind=4,
    PageFooterArea1 kind=7, ReportFooterArea1 kind=8 — one section each.
    Cost if wrong: none.

  CARRY TO TASK 5: ISCRArea.Kind gives the band DIRECTLY (CrAreaSectionKindEnum). Task 5's planned
    ClassifySection() name-guessing heuristic is now known to be unnecessary AND wrong — RAS names sections
    Section1..N with no band encoded. Task 5 must map band from area.Kind, building a section-name -> band
    dictionary by walking Areas before the section loop. My plan already described this as the fallback fix;
    it is now the PRIMARY path.

  Ruling: accepted both implementer deviations. (a) Program.cs stub needed because the brief's csproj sets
    OutputType=Exe with no entry point until Task 8 — keep stub, comment that Task 8 replaces it.
    (b) Test csproj needs direct <Reference> HintPaths because ProjectReference does not flow transitive
    <Reference> items to compile. Both are correct .NET behaviour, not workarounds. Cost if wrong: none.

  New reference requirement: CrystalDecisions.CrystalReports.Engine + CrystalDecisions.Shared from the GAC at
    11.5.3700.0 (note: DIFFERENT build number from the RAS assemblies at 11.5.3300.0 — they interoperate,
    proven above). Global Constraint 2 unchanged: still 11.5 only, never the 13.x NuGet packages.

  MY ERROR (8b1128f): I ran `git add -A` to commit plan-doc corrections while the Task 4 implementer was
    actively editing source files. The commit swept in its in-progress CrystalSession.cs / Program.cs /
    csproj / test edits under a docs-only message.
  Ruling: LEAVE IT. Nothing is lost — the work is committed, not destroyed, and the implementer's remaining
    delta will commit normally on top. Soft-resetting or amending would rewrite the index and worktree
    underneath a subagent that is actively writing those exact files, which is the genuinely destructive
    option. The only harm is a misleading commit message. Cost if wrong: git history for Task 4 is slightly
    muddled across three commits instead of two; no code impact.
  Consequence for review: Task 4's review package BASE stays 5bb8c15 (pre-ec7ab03), so the reviewer sees the
    implementer's complete work regardless of which commit each hunk landed in.
  Lesson applied for the rest of this run: use targeted `git add <paths>` for controller-side doc commits
    while any implementer is live. Never `git add -A`.

## ============ PAUSE POINT (user-requested) ============

To resume: re-invoke superpowers:subagent-driven-development with
  D:/VibeyReports/docs/superpowers/plans/2026-08-31-vibey-reports.md
It will find this ledger and pick up at the first task with no "complete" line.

State at pause:
  Repo:   D:/VibeyReports   Branch: feat/vibey-reports   NO REMOTE (user constraint: local only, never push)
  Done:   Task 1, Task 2, Task 3  (all reviewed clean; Task 3 needed 1 fix round)
  Next:   Task 5 (AMENDED by Addendum A — must also extract ReportSchema.AvailableFields)
  Then:   Task 6, Task 6b (addField), Task 7, Task 8, Task 9, Task 10, Task 11

Briefs already generated in this workspace: task-1..task-5-brief.md
  NOTE: task-5-brief.md was generated BEFORE I corrected the plan. REGENERATE IT before dispatching Task 5:
    scripts/task-brief <plan> 5
  The stale copy still contains the wrong ReportDefinition.Sections walk and ClassifySection heuristic.
  It must ALSO be supplemented with the Task 5 amendment from Addendum A (AvailableFields extraction),
  which lives at the END of the plan file, not in the Task 5 section the brief script extracts.

Hard-won facts that MUST reach every remaining Crystal task (all measured on this machine, not reasoned):
  1. NEVER `new ReportClientDocument()` — it hangs on a TCP self-connect to port 1566.
     Open via `new ReportDocument(); .Load(path)` then take `.ReportClientDocument`. ~2ms, no network.
  2. ISCRReportDefinition has NO Sections property. Walk `.Areas`; each ISCRArea has `.Sections` and `.Kind`.
     Section names are Section1..N with no band encoded — map the band from area.Kind.
     SampleReport.rpt: 5 areas, kind 1/2/4/7/8 (RH/PH/Detail/PF/RF), one section each.
  3. Write path VERIFIED end to end through the engine bridge:
     GetAllReportObjects -> Clone(true) -> mutate -> Modify(old,new) -> SaveAs(name,dir,0) -> reopen
     -> change persisted; source file untouched.
  4. Worker + its test project must both be net48 + PlatformTarget x86.
  5. Engine assemblies are GAC 11.5.3700.0; RAS assemblies are 11.5.3300.0. Different build numbers,
     they interoperate. Never substitute the CrystalReports.* 13.x NuGet packages for either.
  6. ProjectReference does not flow transitive <Reference> items — test csprojs need their own HintPaths.
  7. Repo-scoped NuGet.Config (<clear/> + nuget.org) is REQUIRED; the machine-wide config has a private
     PeoplesHR feed that 401s and blocks all restore.
  8. Controller must use targeted `git add <paths>`, never `git add -A`, while an implementer is live.

Reusable 32-bit verification harness (how I proved the above):
  C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script>
  Scripts kept at: <scratchpad>/testengine.ps1 and <scratchpad>/testwrite.ps1
  Use this to settle any future "does the SDK really do X" question empirically instead of guessing.

Task 4: fix round 1/5 (B1+B2 addressed; commits ec7ab03..8b1128f) — 4 passed, 0 failed, verified across a
  clean rebuild. Implementer independently flagged my git add -A error back to me. Good catch on its part.
Task 4: complete (commits 5bb8c15..8b1128f, review clean — spec OK, quality approved)

  Ruling: the Important finding (Dispose() calls _engineDoc.Close() but never _engineDoc.Dispose(), risking
    COM accumulation) is PLAN-MANDATED — my own task-4-findings-round1.md specified exactly that Dispose
    shape, and the reviewer said so explicitly. So it is mine to rule on, not to loop on.
    Decision: CARRY TO TASK 8 rather than fix now. Reasoning: Task 9's CrystalWorkerClient spawns ONE WORKER
    PROCESS PER REQUEST by design ("keeps legacy COM state from leaking between operations"), so in
    production the process exits after a single report and accumulation cannot occur. The exposure is real
    only inside multi-report TEST processes (Tasks 6 and 8), which are also short-lived. Task 8 is where the
    process-lifetime question actually gets decided, so the fix belongs there with that context.
    Cost if wrong: a test process holding many reports could exhaust COM resources; would surface as a flaky
    or failing Task 6/8 test run, and the fix is 2 lines (add _engineDoc.Dispose() after Close()).

  Task 4: minor (deferred): no try/finally around `new ReportDocument()` + `.Load()` in Open — a Load failure
    on a corrupt .rpt leaks the ReportDocument. Error path only, untested.
  Task 4: minor (deferred): NO TEST covers "SaveAs refuses to overwrite the SOURCE even with overwrite:true".
    The guard exists and is correctly ordered before the overwrite check (reviewer traced it), but a named
    global constraint has zero direct coverage. Inherited from the brief's own test list, not introduced.
    Worth adding when Task 6 or 8 next touches CrystalSession.
  Task 4: minor (deferred): both csprojs define $(CrystalDir) independently instead of sharing via
    Directory.Build.props. Trivial duplication in a 2-project solution.

## Status at pause: Tasks 1,2,3,4 COMPLETE and reviewed clean. Next = Task 5 (amended). See PAUSE POINT above.

## RESUMED (session 2)
Verified on resume: Tasks 1-4 complete, tree clean, branch feat/vibey-reports, no remote. Ledger matches git.
Regenerated task-5-brief.md from the CORRECTED plan (stale copy had the wrong ReportDefinition.Sections walk);
  verified 0 stale references. Extracted task-5-supplement.md (Addendum A's Task 5 amendment) since the brief
  script cannot reach the addendum at the end of the file.
Ruling: re-extracted ALL remaining briefs by explicit LINE RANGE instead of heading-string matching. The
  script's boundary detection ran past the end of Task 6 and swallowed Task 6b plus the whole addendum
  (901 lines instead of 553) — that would have handed the Task 6 implementer work belonging to Task 6b and
  caused scope creep the reviewer would then have flagged. Verified each brief now contains exactly one
  "## Task" heading. Cost if wrong: a brief could clip content; mitigated by the per-brief heading-count check.

Task 5: implemented (commit b2b785b) — 14 passed, 0 failed, 7min against real SDK.
  Implementer found 3 brief snippets that do not compile against the real assembly; I verified 2 by
  reflection and both are correct:
    - FontColor.Font is RAS ISCRFont (Name, Size as DECIMAL, Bold, Italic, Underline, Strikethrough,
      Weight, Charset) — NOT System.Drawing.Font.
    - ISCRPageMargins uses Left/Right/Top/Bottom — NOT leftMargin/rightMargin/etc.
    - CrAlignmentEnum lives in ReportDefModel, not CommonObjectModel (comment only, no compile impact).
  The supplement's CrFieldValueTypeEnum mapping needed ZERO changes — every member name existed verbatim.

Task 5: review round 1 — spec FAILED. 1 Critical + 1 Important.
  MY ERROR: pre-flight Ruling G (PageInfo.WidthTwips must be FULL paper width, not content width) was
    recorded in this ledger but I FAILED TO CARRY IT INTO THE TASK 5 DISPATCH. I put it in the reviewer's
    prompt but not the implementer's. The implementer transcribed the brief's wrong code faithfully.
    The review caught it — the system worked, but this was a controller miss.
  Ruling: F1 (Critical) FIX — PageContentWidth/Height already have margins removed. Measured on
    SampleReport.rpt: content 11186x16118, margins 360 all round, +margins = 11906x16838 = exact A4.
    Task 3's shipped validator computes printable = Width - MarginL - MarginR, so margins were being
    subtracted TWICE, losing 720 twips (0.5in) per axis and wrongly rejecting valid placements.
    Fix = PageContentWidth + Left + Right. I reflected ISCRPrintOptions myself: there is NO direct
    paper-size-in-twips property (only a PaperSize ENUM, which cannot express custom sizes), so
    reconstruction from content+margins is the correct and exact approach.
    Cost if wrong: page dims off by the margin sum; caught by the new test that pins Width > printable.
  Ruling: F2 (Important) FIX — Read_ClassifiesSectionsIntoRecognisableBands is vacuous: "Other" is in its
    allowed set, so reverting to the broken name-based heuristic (which returns "Other" for EVERY section)
    still passes. Added a test asserting the five real bands of SampleReport and NotContain("Other").
    Cost if wrong: none; strictly tightens an existing assertion.
Task 5: minor (deferred): the DB-logon catch branch is never actually exercised (no fixture triggers a
  logon failure) — honestly disclosed by the implementer. Bare `catch` breadth is intentional; comment only.

  CORRECTION to my own carried-forward fact #2: I claimed "section names are Section1..N with no band
    encoded". That is FALSE. I extrapolated it from the AREA names I measured (ReportHeaderArea1,
    PageHeaderArea1, ...) without ever measuring SECTION names. The implementer measured them directly:
    they are {Band}Section1, e.g. ReportHeaderSection1. It refused to add my false assertion and told me
    instead of editing silently — correct behaviour.
    The Areas/Kind fix STANDS regardless: area.Kind is the authoritative source and does not depend on
    naming convention, which may differ across reports and Crystal versions. Only my justification was wrong,
    not the remedy.
Task 5: fix round 1/5 (2 addressed, 0 open — F1 page-size, F2 band test; commits b2b785b..afcc17a)
Task 5: complete (commits 8b1128f..afcc17a, review clean after 1 fix round) — 20 tests, 0 failed
Task 5: minor (deferred): pre-existing Read_ClassifiesSectionsIntoRecognisableBands is still vacuous on its
  own (allows "Other"); the new band test now guards the behaviour, so the old one is dead weight.
Task 5: minor (deferred): DB-logon catch branch still never exercised by any fixture.

CARRY TO TASK 6 (staged in task-6-supplement.md):
  C1 FontColor.Font is mutable RAS ISCRFont (Size is DECIMAL) — brief's System.Drawing.Font code will NOT
     compile. Set properties directly on the clone's font; fallback = construct FontClass and assign.
     UNSETTLED: whether in-place mutation persists through SaveAs. My PS harness cannot late-bind derived
     COM interface members, so Task 6's own persistence test decides it. Fallback documented.
  C2 FindSection must walk Areas (no ReportDefinition.Sections).
  C3 Move/Resize must sync Right/Bottom on Line/Box or endpoints go stale (AddLine/AddBox set them, Move
     does not). New test added for moving a line.

Task 6: implemented (commit 1a2343b) — 30/30 worker tests, 0 failed, ~7min. Contracts 32/32 unaffected.
  Font persistence worked via IN-PLACE ISCRFont mutation on the clone; the FontClass fallback was not needed.
  Implementer changed TEST DATA (not assertions) of Apply_ResizesAnObject... — added a Move-to-top-0 before
  the Resize, because every placed Text/Field object on SampleReport sits flush against its section bottom
  with zero headroom, so a bare resize-taller is correctly rejected by the validator. Disclosed, not silent.

Task 6: review round 1 (opus) — spec OK, quality APPROVED, but with 2 Important gaps. Reviewer ran live RAS
  probes against COPIES of the fixture (hash-verified untouched) rather than reasoning from the diff.
  It independently DISPROVED a concern I raised: Clone(true) deep-copies FontColor.Font, so the font change
  persists because Modify carries it, NOT by aliasing. Mechanism is sound, not fragile.
  Ruling: accepted the implementer's test-data deviation. Reviewer verified the fixture geometry claim exactly
    (PageHeaderSection1 height 442; PrintDate1 at top 221 height 221 -> no headroom). The added Move sets only
    Left(unchanged)/Top, touching neither Width nor Height, so both assertions can still only be satisfied by
    the Resize. Test still proves its name. Cost if wrong: none; assertions untouched.
  Ruling: F1 (Important) FIX IN TASK 3's VALIDATOR — RAS only supports horizontal/vertical lines; an addLine
    with BOTH axes non-zero passes validation then throws a raw COMException, violating the applier's own
    "validator should have rejected it" contract. A model will plausibly emit exactly that. Cross-task fix
    into LayoutPlanValidator.cs. Cost if wrong: none; strictly narrows what is accepted.
  Ruling: F2 (Important) FIX — Apply is NOT transactional. Reviewer demonstrated op1 stays applied after op2
    throws, and CrystalSession.SaveAs has no guard, so a HALF-APPLIED DOCUMENT CAN BE WRITTEN TO DISK. Latent
    only because nothing calls Apply yet; Tasks 8/10 are the callers and a try/catch there would bake in
    silent corruption. Fix = faulted flag on the session that SaveAs refuses. Validation failures must NOT
    set it (they throw before any mutation, document still clean). Cost if wrong: a legitimate save could be
    blocked after a recoverable error; caller re-opens and re-applies, which is the correct workflow anyway.
  Ruling: F3 (Minor, fixing anyway) — o.Format dereferenced unguarded in setAlignment while the reader's own
    read path treats null Format as possible. One line, and an NRE mid-plan would feed F2.
  Ruling: T1-T3 FIX. T1 is the highest-value gap in the whole task: SyncEndpoints in the RESIZE branch has
    ZERO coverage and fails SILENTLY (unlike Move, which throws loudly). Measured: unsynced resize reads back
    W=1440 in memory but persists W=2880 after save+reopen. Deleting SyncEndpoints from Resize keeps all 10
    tests green while writing wrong .rpt files. T2 asserts Right/Bottom directly (currently unobservable via
    ObjectInfo). T3 asserts the source .rpt is byte-identical after Apply — a stated global constraint with
    no coverage at all. Cost if wrong: none; all three strictly add coverage.
Task 6: minor (deferred): duplicate-name resolution diverges (validator last-wins vs applier first-wins,
  theoretical since Crystal enforces unique names); test helper duplication; setAlignment on Line/Box is a
  silent no-op; Apply's return value is always plan.Operations.Count; untested lesser paths (Box move/resize,
  ResizeSection shrink, ParseAlignment other values, empty plan, null-arg guards).

PRE-EMPTIVE DE-RISK OF TASK 7 (done by me while Task 6 fix ran, using the 32-bit harness):
  Verified PDF export end to end against all 4 fixtures BEFORE dispatching Task 7. Two brief defects found:
  Ruling: C1 — ISCRByteArray has NO get_Stream(). The brief's stream plumbing does not compile. Reflected
    the type: it exposes `Byte[] ByteArray`, `Save(path, overwrite)`, Count, AttachArray/DetachArray.
    ExportPdf collapses to `byteArray.ByteArray`. VERIFIED working: SampleReport 23632 bytes, JournalEntry
    41583, PMSV10_IndPerfOverview 49363 — all with %PDF- magic and %%EOF tail.
    NOTE: the customer's real PeoplesHR report (PMSV10) renders cleanly. Cost if wrong: none, measured.
  Ruling: C2 — Documents.rpt throws COMException "Missing parameter values" (it declares parameters with no
    saved values). Do NOT add parameter-supplying code — Global Constraint 5 forbids touching parameters.
    Instead wrap the COMException in an InvalidOperationException naming the report and stating that the
    layout can still be READ AND EDITED even though it cannot be PREVIEWED. This error surfaces to Claude
    through the Task 10 MCP preview tool, so a bare COM message is not good enough.
    Cost if wrong: a renderable report could be misreported as parameterised; the breadth test over 3
    fixtures guards against that.
  Ruling: C3 — the brief's two troubleshooting notes are obsolete and must NOT be followed. get_Stream does
    not exist (C1 is the answer, not a fallback), and NO fixture produces a database logon error, so the
    brief's "mark the test Skip" escape hatch must not be used. No test in Task 7 may be skipped.
  Staged as task-7-supplement.md.
Task 6: fix round 1/5 (6 addressed, 0 open — F1,F2,F3,T1,T2,T3; commits 1a2343b..1164448)
  Contracts 35/35, CrystalWorker 33/33, both foreground. Pre-existing tests: PURE INSERTIONS, 0 deletions
  in both test files — no existing assertion touched.
  Implementer verified T1 genuinely catches the regression by temporarily reverting SyncEndpoints and
  confirming the exact 2880-vs-1440 mismatch the reviewer had measured, then restoring byte-identical.
  Ruling: ACCEPTED the internal seam ApplyOperationsWithoutValidation (+ InternalsVisibleTo scoped to the
    test assembly only). I pre-authorised it because F1 made F2's repro unreachable through public Apply.
    Verified: seam is `internal` not public; public Apply always validates first with no bypass; validation
    failures physically CANNOT mark the session faulted because the faulting try/catch lives inside the
    post-validation method.
    BUT the reviewer correctly notes `internal` does not mean "test-only" — Task 8's Program.cs lives in the
    SAME assembly (src/VibeyReports.CrystalWorker/), so nothing at the type-system level stops a future
    same-assembly caller from bypassing the gate. Today's safeguard is a doc comment and a discouraging name.
    Cost if wrong: a future worker-assembly caller silently skips validation and can corrupt a .rpt.
    MITIGATION: carried as a hard constraint into Tasks 8 and 10 dispatches (below).
Task 6: complete (commits afcc17a..1164448, review clean after 1 fix round)
Task 6: minor (deferred): fix report inaccurately claimed the mid-plan-failure test contains an inline
  source-byte comparison; it does not. Harmless — that test's SaveAs always throws before any write.

CARRY TO TASKS 8 AND 10 (hard constraint): production code in VibeyReports.CrystalWorker MUST call
  LayoutApplier.Apply, NEVER LayoutApplier.ApplyOperationsWithoutValidation. The latter is a test-only seam
  that skips the validator — the project's single safety gate — and it is same-assembly reachable.

Task 6b: implemented (commit 0a0a7a9) — Contracts 40/40, CrystalWorker 34/34.
  Binding path: DIRECT FieldObjectClass construction worked, but ONLY after also setting FieldValueType —
  the brief's exact code threw COMException "The field value type is not valid." Implementer resolved the
  type via the same DB-table walk ReportReader uses, matched by FormulaForm. Discovered by measurement.
  Fixture switched SampleReport -> PMSV10_IndPerfOverview (brief's own escape hatch; SampleReport's only
  Details section is 221 twips, too short). Reviewer confirmed the test still proves its name.

Task 6b: review round 1 (opus) — spec OK, quality NEEDS WORK. 0 Critical, 3 Important.
  SECURITY BOUNDARY TRACED AND SOUND: reviewer could construct NO path by which a fieldRef outside
  AvailableFields reaches the applier, and confirmed ISCRReportObjectController's entire surface is
  Add/Remove/Modify/GetAllReportObjects/GetReportObjectsByKind/AddByName/ImportPicture — no table,
  connection, SQL or link method exists on it at all. Also confirmed fail-closed: ReportReader's catch
  CLEARS AvailableFields, and an empty allowlist rejects every addField.
  Ruling: F1 (Important) FIX — catch(COMException){} swallows the real error, so a field-type failure
    surfaces to the operator as a RENAME error. Throw precisely when ResolveFieldValueType returns null,
    and never discard the COMException. Cost if wrong: none, strictly improves diagnosis.
  Ruling: F2 (Important) DELETE AddFieldByName — MY BRIEF WAS WRONG. I documented AddByName as the
    fallback. The implementer measured that Modify() refuses to rename, and I confirmed by reflection that
    the controller has NO Rename/SetName method at all. So the fallback has NO reachable success case: it
    always throws, after mutating the live document, while destroying the original diagnostic. This is a
    plan-mandated finding and therefore mine to rule on — the empirical evidence invalidates my brief's
    premise. The implementer's instinct to throw rather than silently mis-name was RIGHT; the correct
    conclusion is that the path should not exist. Deleting it also removes two latent bugs: AddByName
    creates TWO objects (field + heading) and the code only located the field, leaving an unnamed heading
    at Left=10686 (past a typical 10800 printable width); and op.NewName was passed as headingText, so the
    heading would have printed the plan's OBJECT NAME as visible report text.
    Cost if wrong: if some RAS build did allow renaming, we lose a path that never worked here anyway.
  Ruling: F3 (Important) FIX — validator matches fieldRef OrdinalIgnoreCase but the applier binds the
    CALLER's string verbatim, while LayoutPlan.cs documents "must exactly match". A case-variant like
    {command.STAGE_NAME} vs {Command.stage_name} passes validation and binds a DataSource RAS may accept
    but not resolve -> a field that RENDERS BLANK, which the brief itself calls worse than an error.
    Fix = bind the canonical FormulaForm, keep the forgiving match. NOT a security escape (a case-variant
    of an allowlisted field is still allowlisted). Cost if wrong: none.
  Ruling: T1-T4 FIX. T1 is the priority: the security boundary is currently tested ONLY against hand-rolled
    schemas calling the validator directly — nothing asserts Apply rejects a bogus fieldRef against a REAL
    report and leaves the document unmutated. A refactor reordering Apply to mutate-before-validate would
    keep all 40 Contracts tests green. T2 pins fail-closed on an EMPTY allowlist (the exact state a logon
    failure produces). T3 exercises >1 field value type (the whole deviation was about value types, yet
    only AvailableFields.First() is covered). T4 proves addField+setBold end to end.
Task 6b: minor (deferred): brief Step 10 (Task 10 tool description must mention addField/fieldRef) —
  carried into Task 10's dispatch instead, since Task 10 does not exist yet.
Task 6b: minor (deferred): AvailableFields.First() gives an opaque error if the reader cleared the list;
  asserting FieldValueType itself would need a ReportReader/ObjectInfo extension.
Task 6b: fix round 1/5 (8 addressed, 0 open — F1-F4, T1-T4; commits 0a0a7a9..4761e08)
  Contracts 41/41, CrystalWorker 37/37. AddFieldByName DELETED. fieldRef now binds the CANONICAL
  FormulaForm, not the caller's string. One pre-existing test strengthened (0,0 -> 200,40 with assertions),
  which the findings doc itself instructed.
  NEW SDK FACT discovered while implementing T4: a freshly-constructed FieldObjectClass comes back with
  FontColor == NULL, unlike a field already present in an .rpt. So setBold immediately after addField threw
  "has no font object". Implementer fixed by constructing a default FontColor/Font (Arial 10).
Task 6b: complete (commits 1164448..4761e08, review clean after 1 fix round)

Ruling: the AddText FontColor gap is LOAD-BEARING and will NOT be deferred to a background chip.
  I verified the source directly: AddText constructs TextObjectClass with NO FontColor, while the fixed
  AddField now sets one. So `addText` then `setBold` in a single plan throws "has no font object."
  That combination IS THE USER'S PRIMARY STATED USE CASE — the screenshot they sent is four BOLD text
  headings over a details band. Shipping this would break the first thing they try.
  The skill's rule for out-of-scope observations is "ledger as deferred minor", but this is neither minor
  nor out of scope in spirit: it is a Critical defect in Task 6's shipped code, in the same method family
  the 6b fix just corrected, breaking the documented primary workflow. Dispatching a dedicated fix.
  Cost if wrong: a small extra task; the alternative is a broken headline feature.
Ruling: also fixing the reviewer's related point — Arial 10 is an ARBITRARY hardcoded default. A heading
  added to a report whose other objects use a different font would silently render inconsistent, and the
  AI would have to setFont every single time to compensate. Better default: inherit from an existing
  Text/Field object in the SAME section, falling back to Arial 10 only when the section has none.
  Cost if wrong: an inherited font is occasionally not what was wanted; setFont still overrides it.

Task 6c (AddText font defaulting): implemented (commit 8767425) — worker suite 40/40, foreground.
  MY PREMISE WAS HALF WRONG, and the implementer proved it by reverting each fix independently:
    - AddField DID throw "has no font object" pre-fix. Confirmed.
    - AddText did NOT throw. A freshly constructed TextObjectClass's outer FontColor is NOT null after
      Add(); RAS supplies its own default "MS Shell Dlg" (a UI dialog font, not even Arial). So the defect
      was SILENT WRONG FONT, not a crash. Arguably worse than what I described, but not what I described.
  NEW SDK FACT (significant): a text object's actually-persisted font lives on
    ISCRParagraphTextElement.FontColor (the text RUN inside a Paragraph), NOT only on
    ISCRTextObject.FontColor. At Add() time the outer value is SILENTLY DISCARDED unless the paragraph
    element carries its own matching FontColor. AddText now sets both, with separate FontClass instances.
    AddField has no paragraph/run structure and needs only the single top-level assignment (verified).
  IMPORTANT COROLLARY (checked, no action needed): once an object is already in the document, a later
    WithFont mutation via ModifyObject/Modify DOES correctly update what persists. So setFont/setBold on
    PRE-EXISTING text objects was never broken — only the initial Add() path was.
  Ruling: replaced AddField's hardcoded Arial 10 with DefaultFontColorFor(doc, section) as well, so both
    add paths inherit the section's existing font. Measured across all 4 fixtures to pick a section where
    the assertion is meaningful: PMSV10_IndPerfOverview / PageHeaderSection1 is the only section whose
    existing objects all share one NON-Arial font (Segoe UI), so a hardcoded-Arial default fails it
    outright. Cost if wrong: an inherited font is occasionally not what was wanted; setFont overrides it.
Task 6c: minor (deferred): ReportReader reads a text object's font from the OUTER TextObject.FontColor,
  but the rendered font lives on the paragraph RUN. For text objects with mixed per-run formatting the
  reader would report only the outer font. Unusual in HR reports; our own AddText now keeps both in sync.
Task 6c: review round 1 — spec OK, quality APPROVED. 1 Important (documentation accuracy only).
  Reviewer credited the revert-and-rerun methodology and confirmed: DefaultFontColorFor copies all 8 font
  properties into FRESH instances; AddText's two FontColorClass instances are genuinely independent (two
  separate helper calls, no aliasing); scope respected; Contracts untouched; internal seam not widened.
  Ruling: Important-1 FIX (comments only, no code change) — the XML doc comments on the two round-trip
    tests still assert my ORIGINAL WRONG premise ("comes back with FontColor == null... SetBold throws").
    The implementer's own investigation disproved that for AddText, and it verified both tests pass against
    reverted code. So they are coverage tests, NOT regression tests for a crash. The accurate account lives
    only in the report file, which nobody reading the test file will see. Concrete risk: someone later
    simplifies AddText believing these tests guard the null-FontColor path — they do not.
    Only Apply_AddedTextInheritsTheFontOfExistingObjectsInItsSection is a genuine regression test.
    Cost if wrong: none, comments only.
Task 6c: minor (deferred): no test covers DefaultFontColorFor's Arial/10pt fallback branch (would need a
  section with zero Text/Field objects). Reviewer could not independently verify the fixture font survey
  from static inspection (binary .rpt does not expose font names as scannable plaintext); it accepted the
  implementer's measurement, and so do I — the same methodology correctly found two other real SDK facts.
Task 6c: fix round 1/5 (1 addressed, 0 open — doc comments; commits 8767425..f832c2b) comments-only, verified
Task 6c: complete (commits 4761e08..f832c2b, review clean after 1 fix round) — worker suite 40/40

Task 7: implemented (commit 2a61432) — worker suite 46 total, 0 failed, 0 skipped, 6m56s.
  Followed the supplement over the brief. Implementer INDEPENDENTLY reflected the assemblies and confirmed
  PrintOutputController.Export returns CommonObjectModel.ByteArray with NO stream-returning member anywhere
  — and that the brief's own suggested fallback (ItemArray/indexer) is ALSO a dead end. Useful for 8/10.
Task 7: review round 1 — spec OK, quality APPROVED. 1 Important, 1 Minor.
  Ruling: Important FIX — the COMException IS preserved as InnerException in code, but NO test asserts it.
    A regression writing `new InvalidOperationException(message)` without the `ex` argument would pass every
    test in the file. This matters because Task 10 surfaces these errors to Claude through the MCP preview
    tool, and the inner COM message is the part that says WHY the render failed. One-line assertion.
    Cost if wrong: none.
Task 7: minor (deferred): report's test-count arithmetic is internally inconsistent (says 5 new, but the
  diff adds 2+1+1 Theory with 3 InlineData = 6 executed results; 41+6=47 vs the 46 reported). Code unaffected.
Task 7: minor (deferred): no test for ArgumentNullException on null session; the null-ByteArray and
  zero-length guards are unreachable from the four real fixtures. Defensive code, acceptable uncovered.
Task 7: fix round 1/5 (1 addressed, 0 open — inner-exception assertion; commits 2a61432..bf460d8)
Task 7: complete (commits f832c2b..bf460d8, review clean after 1 fix round)

PRE-EMPTIVE DE-RISK OF TASK 8 (staged as task-8-supplement.md before dispatch):
  Ruling: C1 (Critical) — my brief sets `Console.OutputEncoding = Encoding.UTF8`, which is constructed with
    encoderShouldEmitUTF8Identifier:true and can emit a UTF-8 BOM (EF BB BF) ahead of the JSON. Task 9's
    client would then throw JsonException "'0xEF' is an invalid start of a value" — across a process
    boundary that reads as a WORKER CRASH and is nasty to diagnose. Worse, it is INVISIBLE to a string-based
    test because StreamReader silently swallows the BOM. Fix = new UTF8Encoding(false), plus a test that
    reads stdout as RAW BYTES and asserts byte[0] == '{'. Cost if wrong: none.
  Ruling: C2 — Task 4's placeholder Program.cs must be REPLACED, not supplemented; a second Main is CS0017.
  Ruling: C3 (Critical) — Program.cs lives in the SAME ASSEMBLY as LayoutApplier, so the compiler will not
    stop it calling the internal ApplyOperationsWithoutValidation seam that bypasses the validator.
    Carried as an explicit prohibition.
  Ruling: C4 — a mid-plan failure faults the session and SaveAs refuses, so NO output file must exist.
    Required as an assertion, not just prose.
  Ruling: C5 — ReportSchema.AvailableFields must pass through the read response unfiltered; it is the
    allowlist for addField.
  Ruling: C6 — ReportRenderer's actionable error message (names the report, says layout can still be read
    and edited) must survive into WorkerResponse.Error, not be replaced by a generic string.
  Ruling: C7 — the brief's "20 total" test count is stale; the suite is already at 46. Verify 0 failed.

PRE-EMPTIVE DE-RISK OF TASK 9 (staged as task-9-supplement.md), sourced from a WORKING MCP server already
  on this machine: D:\PHR-X-DB-MCP-SERVER\src\PeoplesHR.DBMCPServer\ (same SDK version, house conventions).
  Ruling: C1 (CRITICAL) — my brief sets LogToStandardErrorThreshold on an AddConsole it adds, but never calls
    builder.Logging.ClearProviders(). Host.CreateApplicationBuilder ALREADY registers a default Console
    provider that writes to STDOUT. MCP speaks JSON-RPC over stdout, so any log line there CORRUPTS THE
    PROTOCOL and the server looks broken or never connects. The working server calls ClearProviders() first.
    Cost if wrong: none — ClearProviders is strictly correct here.
  Ruling: C2 — use WithTools<ReportTools>() not WithToolsFromAssembly(). Assembly scanning silently registers
    NOTHING if the attribute is missing or the type is not public, and you only find out when the tools fail
    to appear in the client. Also set ServerInfo (brief omits it).
  Ruling: C3 — Program returns int; write startup failures (e.g. WorkerLocator.Find throwing) to stderr and
    return 1 rather than letting an unhandled exception escape.
  Ruling: C4 — WorkerLocator gains a `worker/` subdirectory probe (pre-flight Ruling Q: Task 11 publishes the
    net10 server and net48 worker to SEPARATE dirs because both carry VibeyReports.Contracts.dll and the
    net48 dependency set would overwrite files the net10 app needs).
  Ruling: C6 — worker failures are DATA not exceptions: exit 0 with ok:false is a normal round trip and must
    not throw; non-zero exit or empty stdout must surface exit code + truncated stderr so the cause reaches
    Claude instead of being lost.
  VERIFIED for Task 10: ModelContextProtocol.Core 1.2.0 really does export ContentBlock, TextContentBlock,
    ImageContentBlock, AudioContentBlock and CallToolResult. The plan's image-preview approach is sound.
    Tool convention from the working server: [McpServerTool(Name="...", ReadOnly=true)] + [Description] on
    method AND each parameter + trailing CancellationToken cancellationToken = default.

  MY ERROR (caught immediately): the previous attempt to write these rulings SILENTLY FAILED because the
    shell cwd had persisted from `cd /d/PHR-X-DB-MCP-SERVER/...` when I read the working MCP server, so the
    relative ledger path did not resolve. The `echo ledgered` still printed, which masked it. Repo and ledger
    were intact. Lesson applied: use ABSOLUTE paths for ledger writes, never a relative WS path.

Task 8: implemented (commit fc3fc3a) — Contracts 44/44, CrystalWorker 53/53, foreground, no hangs.
  BOM check CONFIRMED: reads stdout via BaseStream as raw bytes, asserts raw[0]==0x7B. My C1 Critical
  correction was warranted and is now pinned by a test a string comparison could not have caught.
  Exit-code-0-on-ok:false CONFIRMED across invalid plan / render failure / unknown command.
Task 8: review round 1 — spec OK, quality APPROVED with 3 Important. No Critical.
  Ruling: F1 (Important) FIX — Program.cs reads the schema AFTER SaveAs, on the same un-reopened session.
    ReportReader can throw on freshly-added objects read back without a reopen (LayoutApplier's own comments
    record exactly that RAS quirk, which is why ApplyAndReread closes and reopens). So an apply containing
    addField can succeed, WRITE THE FILE, then throw during the re-read -> ok:false WITH A REAL .rpt ON DISK.
    The caller cannot distinguish that from a clean failure. Fix = read the schema BEFORE SaveAs; the
    in-memory document is identical, and it makes "ok:false implies nothing was written" structurally true.
    Cost if wrong: none; strictly reorders two calls with no behavioural loss.
  Ruling: F2 (Important) FIX — AddField wraps its Add() in catch(COMException) but AddText/AddLine/AddBox do
    not, so a RAS rejection reaches Claude as a raw COM message naming no operation, object or section.
    Cost if wrong: none; strictly improves diagnosis.
  Ruling: F3 (Important) FIX THE REPORT, not the code — the implementer claimed no validator-legal plan can
    reach a mid-plan failure. The reviewer found a counterexample: ResolveDbField re-walks the live DB tables
    at APPLY time with NO try/catch, on the same surface ReportReader deliberately wraps in a bare catch
    BECAUSE IT IS FLAKY. A DB flake between validate and apply is exactly such a path.
    HOWEVER I UPHOLD the decision not to write that test: the trigger is live-DB nondeterminism and the test
    would be flaky. And the STRUCTURAL guarantee is actually STRONGER than claimed — nothing catches between
    Apply and SaveAs, so it holds for ANY mid-plan fault, not just geometry. Only the framing was wrong.
    Cost if wrong: none; documentation accuracy.
  Ruling: T1/T2 FIX — AvailableFields has NO end-to-end assertion despite being the addField allowlist (a
    worker that dropped it would pass everything), and the only success-path apply test uses setBold, so no
    ADD operation is exercised through the worker process at all — which is precisely where F1's risk lives.
Task 8: minor (deferred): Run/RunRaw boilerplate duplication; Main's final Serialize/Write sits outside the
  try/catch (arguably correct — a crash is the right signal for "could not produce a response");
  BOM-less behaviour verified once in this locale only.
Task 8: fix round 1 agent STALLED (harness stream watchdog, 600s no progress) — NOT a reasoning failure.
  State assessed by me directly: F1 DONE (Program.cs reads schema at :97 then SaveAs at :103 — correct
  order). F2 NOT done (only the pre-existing AddField has COMException handling; AddText/AddLine/AddBox
  have none). T1/T2 not added. F3 report not corrected. Working tree compiles clean.
  Ruling: dispatch a FRESH implementer for the remainder rather than resuming the stalled one — it failed on
  a harness watchdog and may stall again; the remaining work is well-specified and does not need its context.
  Uncommitted F1 work is preserved on disk and the fresh agent is told to keep it. Cost if wrong: a small
  amount of duplicated orientation.
  MY ERROR (corrected by the implementer): I told the fresh agent "F2 NOT done — only AddField has
    COMException handling." WRONG. The stalled agent HAD completed F2, by factoring the handling into a
    shared AddReportObject(doc, section, obj, action, newName) helper that AddText/AddLine/AddBox all call.
    My grep searched each method BODY for "COMException" and so missed the call graph. The fresh agent
    verified before acting and made no F2 changes rather than blindly re-implementing — correct behaviour.
    I re-verified the helper myself: it wraps COMException with action + newName + section and preserves
    InnerException. Lesson: grep the call graph, not just method bodies, before asserting work is missing.
Task 8: fix round 1/5 (5 addressed, 0 open — F1,F2,F3,T1,T2; commits fc3fc3a..11fb977)
  Contracts 44/44, CrystalWorker 55/55. Pre-existing tests: 7 deletions total, all accounted for by the 3
  replaced Add() call sites and the Program.cs reorder — no existing test body touched.
  F1 verified structurally: nothing is caught between Apply and SaveAs, so ANY exception skips SaveAs and
  "ok:false implies no output file" now holds for the whole apply command.
Task 8: complete (commits bf460d8..11fb977, review clean after 1 fix round)

Task 9: implemented (commit d873c6b) — Mcp suite 5/5 against the real worker exe and fixture reports.
  ReportTools created EMPTY (attributed, .WithTools<ReportTools>() wired) per the supplement's option, so
  the registration path is exercised now rather than deferring a compile risk to Task 10.
Task 9: review round 1 — spec OK, quality APPROVED. 0 Critical, 3 Important.
  Reviewer confirmed C1 stdout discipline correct (ClearProviders before AddConsole, line-for-line matching
  the working reference server), architectural separation intact (no Crystal reference anywhere in the Mcp
  project), and the worker/ probe present in the right order.
  Ruling: UPHELD the implementer's ExitCode!=0 deviation. Reviewer traced the worker's Main and confirmed
    exit 0 on EVERY path that writes a response — including ok:false — so a non-zero exit is by contract
    disjoint from a legitimate ok:false and the guard cannot discard a good response. It defends against the
    native COM crash Crystal is prone to, which managed catch cannot intercept. Cost if wrong: none.
  Ruling: UPHELD the empty ReportTools seam. The supplement offered it explicitly; zero tool methods means
    zero scope creep into Task 10, while the .WithTools<T>() wiring is proven now.
  Ruling: F1 (Important) FIX — StandardInputEncoding is unset, so .NET falls back to Console.InputEncoding
    (typically the OEM/ANSI codepage on Windows) while stdout/stderr are explicitly UTF-8. The JSON going IN
    is written in a different encoding from the JSON coming back. THIS BUG IS IN MY BRIEF'S TEMPLATE TOO.
    Concrete risk: a report path or object name with non-ASCII characters corrupts or fails to deserialize —
    and for an HR product deployed across the Philippines and Sri Lanka that is not hypothetical.
    Fix BOTH sides of the pipe (client StandardInputEncoding + worker's stdin read) plus a round-trip test
    using a non-ASCII filename. Cost if wrong: none.
  Ruling: T1 (Important) FIX — the worker/ probe I added in C4 has NO discriminating test; the implementer's
    own run matched via the walk-up FALLBACK instead. So the probe could be broken, removed or mis-ordered
    and every test would still pass. Also pin that VIBEY_WORKER_PATH wins and fails loudly when missing.
  Ruling: T2 (Important) FIX — the timeout/kill, malformed-JSON and ExitCode!=0 branches have ZERO coverage.
    These are exactly the paths that decide whether Claude gets a useful message or a mystery. Specified a
    small fake-worker console project keyed off reportPath (HANG/GARBAGE/CRASH) so the tests are
    deterministic and fast. Cost if wrong: a little extra test scaffolding.
Task 9: minor (deferred): no automated test for Program.cs ClearProviders ordering/exit codes/ServerInfo
  (manual smoke tests stand); InvokeAsync conflates the caller's CancellationToken with its internal timeout
  so an externally-cancelled call reports a timeout message (revisit when Task 10 wires MCP cancellation);
  stdoutTask/stderrTask left unawaited after Kill().
Task 9: fix round 1/5 (3 addressed, 0 open — F1,T1,T2; commits d873c6b..ec6673c)
  worker 55/55, MCP 13/13. F1 fixed BOTH sides: client StandardInputEncoding + worker raw-stream BOM-less
  read (Console.InputEncoding assignment throws on redirected stdin, so the raw StreamReader was correct).
  NON-ASCII ROUND TRIP CONFIRMED WORKING: vibey_café_niño_<guid>.rpt opens and the path survives intact —
  Crystal itself has no problem with such paths, so this was purely our encoding bug.
  New tests/VibeyReports.FakeWorker project (net10.0, IsPackable=false, no Crystal ref, under tests/, not
  referenced by any src/ project) makes the timeout/garbage/crash branches deterministic. Timeout test does a
  REAL orphan check (polls GetProcessesByName until empty), not a post-Kill assumption.
Task 9: complete (commits 11fb977..ec6673c, review clean after 1 fix round)

Task 10: implemented (commit b97e499) — MCP 19/19, Contracts 44/44, worker 55/55.
  BOTH SDK deviations independently VERIFIED CORRECT and forced by the real API:
    - PDFtoImage 5.4.0 ToImage has NO overload without `password`, and returns SKBitmap directly, so the
      brief's SKImage.FromBitmap step was genuinely dead code.
    - ImageContentBlock.Data is ReadOnlyMemory<byte> documented as base64 BYTES, so my brief's
      Convert.ToBase64String would have DOUBLE-ENCODED. FromBytes (raw bytes, lazy encode) is correct.
      That is the 9th defect in my own plan found by measurement.
Task 10: review round 1 (opus) — spec OK, quality APPROVED. 0 Critical, 4 Important.
  Reviewer verified end to end that ValidationErrors reach the agent INTACT (the worker puts the generic
  string in Error and the actionable content ONLY in ValidationErrors, so dropping it would have destroyed
  the retry signal), and that the unpreviewable-report message survives the whole chain verbatim.
  Ruling: F1 (Important) FIX — the tool descriptions ARE the API documentation here, because planJson is an
    opaque string parameter and the JSON schema tells the agent nothing about operation shape. They omit the
    value-carrying property for FIVE of eleven actions: text (addText), heightTwips (resizeSection),
    fontName, fontSizePt, alignment. An agent cannot guess `text` from "newName and full geometry".
    Also adding the ITERATION IDIOM (preview the outputPath, not the source reportPath — otherwise the agent
    previews the unmodified original and concludes its edit did nothing) and apply_layout's success shape
    (it returns the refreshed schema, letting the agent skip a re-read). Cost if wrong: none.
  Ruling: F2 (Important) FIX — dpi is clamped only on the FLOOR. A required model-supplied int with no
    ceiling: Letter at 600dpi is ~134MB, at 2400dpi ~2.1GB. PDFtoImage's own docs warn that oversized output
    causes "corrupted images (e.g. missing text) or crashes". The corrupted mode is the dangerous one — a
    silently TEXT-LESS preview is the worst possible input to a tool whose whole job is layout inspection,
    and the agent would try to fix a problem that does not exist. Clamp 72-300, default 96.
  Ruling: F3/F4 (Minor, fixing) — Encode can return null (NRE for direct callers); and CA1416 is silenced
    properly at the ASSEMBLY level, not method level. The implementer's "annotating moved it upstream" was
    right about method-level (propagation is by design) but the chain terminates at the assembly boundary.
  Ruling: T1 (Important) FIX — preview_report has ZERO tests. It is the tool carrying the entire visual
    feedback loop. Three distinct regressions would keep 19/19 green: dropping the image block entirely,
    dropping IsError, or double-encoding the PNG into valid-looking garbage. The FakeWorker infrastructure
    for a fast Crystal-free test already exists and was not used.
  Ruling: T2 (Important) FIX — the validation-error test asserts only that "NotReal" appears, which passes
    INCIDENTALLY. A refactor flattening the array into a joined string would keep it green while destroying
    operationIndex and the array shape — exactly the "summarised away" regression the brief warns about.
Task 10: minor (deferred): inconsistent error protocol across the three tools (ok:false payload for two,
  IsError for preview_report — both brief-specified); ApplyLayout catches only JsonException; overwrite:true
  never exercised through the tool layer; preview image not asserted to scale with dpi; planVersion
  advertised but never enforced.
Task 10: fix round 1/5 (7 addressed, 0 open — F1-F4, T1-T3; commits b97e499..bd7b5d6)
  MCP 22/22, Contracts 44/44, full solution build 0 warnings 0 errors.
  Reviewer read the rewritten apply_layout description AS THE CONSUMING AGENT and confirmed all 11 actions
  are now first-attempt constructible. T1's success test decodes via DecodedData (not Data), which
  specifically catches a double-encoding regression — a base64-reencoded payload would not decode to real
  PNG bytes. T2 now pins operationIndex and the array shape, so flattening ValidationErrors into a joined
  string would fail where the old substring check would not.
  Ruling: ACCEPTED the follow-through of adding [assembly: SupportedOSPlatform("windows")] to the TEST
    project too. Assembly-level annotation is the documented way to terminate by-design propagation, and
    VibeyReports.Mcp.Tests is not a portability boundary — it exclusively drives a project that launches an
    x86 net48 COM worker and can only ever run on Windows. It reports the truth rather than hiding a real
    cross-platform concern. The implementer disclosed the 44-warning ripple transparently with before/after
    build logs. Cost if wrong: none.
Task 10: complete (commits ec6673c..bd7b5d6, review clean after 1 fix round)
Task 10: minor (deferred): PNG quality arg bumped 90->100 (inert either way); VibeyReports.Mcp.csproj carries
  a stale InternalsVisibleTo comment attributing it to "round-1 fix T1" (pre-existing, not this diff).

Task 11: implemented (commit c8a107c) — ALL THREE SUITES PASS: Contracts 44/44, Mcp 22/22,
  CrystalWorker 55/55 = 121 total, 0 failed, foreground, ~8m20s.
  Published layout verified by me independently: dist\VibeyReports.Mcp.exe (162KB) and
  dist\worker\VibeyReports.CrystalWorker.exe (26KB), no stray D:\dist, dist/ still gitignored, tree clean.
  Worker confirmed GENUINELY x86 by two independent methods: PE header Machine field 0x014C (I386) and
  AssemblyName.ProcessorArchitecture = X86. This matters — the Business Objects COM servers are 32-bit only.
  Published worker answers the protocol on stdin: exit 0, output starts with '{' (no BOM), valid schema.
  Published MCP server starts cleanly and silently, which is itself evidence the worker/ probe resolved
  against the real publish layout (it writes to stderr and returns 1 if it cannot find the worker).
  Ruling: S3 fallback was correct — plain `dotnet publish -c Release -o <dir>` worked first try; the brief's
    `-r win-x86 --self-contained false` was unnecessary because the csproj already pins PlatformTarget=x86.
Task 11: review round 1 — spec OK, quality APPROVED. 0 Critical, 2 Important (both documentation accuracy).
  SCOPE BOUNDARY VERIFIED RESPECTED by the reviewer independently: .claude.json still contains only
  phr-db-mcp and tcm-testcases with no vibey-reports entry, and its mtime PREDATES the dist artifacts.
  No restart claimed. The manual Crystal-open acceptance step is NOT ticked, NOT simulated, NOT claimed —
  README says plainly "Nothing in this section has been ticked or exercised by Task 11."
  Ruling: F1 (Important) FIX — README's supported-operations list omits addField (verbatim copy of the
    brief's stale Step 8 template). The MCP tool description does list it so Claude sees it at runtime, but
    the README is what a future MAINTAINER reads, and addField is the one operation carrying a security
    boundary. Cost if wrong: a maintainer misunderstands the capability surface and its constraint.
  Ruling: F2 (Important) FIX — the $PSScriptRoot footgun (nested powershell.exe -File silently publishes to
    D:\dist; the script's own Test-Path checks still PASS because the wrong location is a real one) is
    recorded ONLY in task-11-report.md, which lives under gitignored .superpowers/. Nobody who later runs
    the script would ever see it. Guard the script AND note it in the README.
Task 11: fix round 1/5 (2 addressed, 0 open — F1,F2; commits c8a107c..83968f2)
  Guard verified by reproducing the EXACT footgun scenario (nested powershell.exe -File): now resolves to
  D:\VibeyReports\dist, no D:\dist recurrence, correct dist\/dist\worker\ split preserved.
Task 11: complete (commits bd7b5d6..83968f2, review clean after 1 fix round)

## ALL 11 TASKS COMPLETE. Proceeding to final whole-branch review.

## FINAL WHOLE-BRANCH REVIEW (opus, 25 commits, 061e467..83968f2)
  Verdict: NOT READY — 1 Blocking, 6 Important, 8 Minor. Safety invariants 4/5 HOLD outright, 1 holds with
  a named exception. Spec criteria 1-6 MET (7 is the manual Crystal open, correctly not performed).
  Verified against PUBLISHED BINARIES not build files: all 10 CrystalDecisions assemblies in dist\worker\ are
  11.5.3300.0 (RAS) or 11.5.3700.0 (engine+Shared), token 692fbea5521e1304; NO Crystal assembly in dist\ at
  all; no CrystalReports.* PackageReference anywhere; worker PE machine 0x014C (I386).

  B1 BLOCKING — THE CROSS-CUTTING DEFECT THE PER-TASK REVIEWS COULD NOT SEE:
    Task 6c MEASURED that a Text object's rendered font lives on the ParagraphTextElement RUN, not the outer
    TextObject.FontColor, and fixed AddText to write BOTH. But WithFont — the modify path used by setFont,
    setFontSize AND setBold — was never revisited and writes ONLY the outer FontColor. ReportReader also
    READS only the outer. So write-outer -> read-outer round-trips GREEN while the rendered font is unchanged.
    The suite is structurally blind to it: LayoutApplierTests' own comment even records that the font test
    "passes even against the pre-fix code (verified directly)" without noticing what that implies.
    Scope: TEXT objects only (FieldObjectClass has no Paragraphs). Titles/labels/headings are Text objects —
    this is the centre of the user's stated use case (four bold column headings).
    NOT YET CERTAIN: RAS's Modify might propagate outer->runs. MUST MEASURE FIRST.
  Ruling: FIX WAVE scoped to correctness + contract items with real user impact: B1 (measure then fix),
    deferred-minor #4 (the ONLY named safety invariant with zero direct coverage), I1 (FieldHeading wrongly
    unfontable), I2 (apply response's schema.reportPath points at the SOURCE — the exact trap its own tool
    description warns against), I3 (overwrite:true deletes before save can fail — destroys the previous good
    output; the one hole in "ok:false implies nothing written"), I5 (exceptions escaping CrystalWorkerClient
    bypass the documented ok:false contract). Also moving the supplements out of gitignored .superpowers/
    into docs/ — nine measured SDK corrections are the most valuable knowledge this project produced and
    they currently vanish on a fresh clone.
  Ruling: DEFERRING I4 (preview exports ALL pages to render page 1 — real perf/OOM risk on long reports, but
    post-merge) and all 8 Minors. Per the skill there is exactly ONE fix wave; these do not earn a slot.
  Deferred-minor triage: 2 must-fix (#4, #13-as-B1), 9 worth-doing-later, 9 won't-fix. Full table in the
    review output; the 9 worth-doing-later are the post-merge backlog.

## FINAL FIX WAVE (commit fc264dc) — 129/129 tests, 0 failed (up from 121)
  B1 VERDICT: FALSE ALARM, no code change. MEASURED, not assumed: setBold and setFont+setFontSize applied to
    an EXISTING Text object (Text27 in PMSV10_IndPerfOverview.rpt) through the real LayoutApplier.Apply ->
    SaveAs -> reopen path, then the outer TextObject.FontColor.Font AND the paragraph run's
    ParagraphTextElement.FontColor.Font read via two genuinely different object-graph paths. Bold went
    false->true on BOTH simultaneously; Name/Size landed on both. RAS's Modify DOES propagate outer->run.
    Re-reviewer independently corroborated Text27 is a pre-existing object using task-6c-report.md, written
    before this wave. Insisting on measurement before fixing was the right call — the "fix" would have been
    applied to a non-problem.
  F2-F7 all ADDRESSED. F3's fixture claim independently proven: the committed test does
    .First(o => o.Kind == "FieldHeading") with no fallback against SampleReport.rpt, so a 129/129 pass IS
    the proof that such objects exist.
  Ruling: ACCEPTED the implementer's correction of its OWN wrong test assumption — SaveAs is NOT
    byte-deterministic across two independent saves (Crystal embeds a timestamp in the OLE compound file).
    It changed the test to compare via re-read. Verified this does NOT undermine ApplyAndReread, which
    byte-compares the untouched SOURCE against itself across one Apply — a structurally different claim.
  Ruling: PARK the one residual gap rather than open a second fix wave (the skill allows exactly one).
    In SaveAs, if File.Delete succeeds but the following File.Move then throws, the catch deletes the temp
    too, leaving neither the old good file nor the new one. Strictly NARROWER than the bug F5 fixed (needs
    Delete to succeed and Move to then independently fail), and pre-existing code had a worse version of the
    same problem. Not a regression. Surfacing it to the user instead of blocking on it.
    Cost if wrong: a rare failed overwrite loses the previous output; recoverable by re-running apply_layout.

## PROJECT COMPLETE — all 11 tasks + final review + one fix wave. Handing over.

---

## Post-merge findings (2026-09-01, during branch finalisation)

Three issues surfaced while verifying the merged result. None is a regression from the merge —
`git diff main feat/vibey-reports` was empty, i.e. the merged tree is byte-identical to the tree
that passed 129/129 immediately beforehand.

### PM1 — `read_report` can BLOCK (not fail) when the report's database is unreachable

**Severity: Important. Product defect, not a test issue.**

With the VPN down, `sgdev01db02.cloud` did not resolve and Crystal's OLE DB layer blocked
indefinitely inside database field enumeration — zero CPU, waiting on a socket that would never
answer. Two suites hung for 28+ minutes. Identified via `dotnet test --blame-hang`, which named
`Read_SucceedsEvenWhenFieldEnumerationCannotReachTheDatabase` and
`ExportPdf_ProducesAValidPdfForEveryRenderableFixture`.

Task 5 carried an explicit requirement that *reading a report's layout must never require a live
database connection*. The implementation honours that for **failure** — field enumeration is wrapped
in a deliberate bare catch, so a connection *error* leaves `AvailableFields` empty and the read still
succeeds. But a catch does nothing about a call that never returns. The requirement is therefore not
truly met.

Note the irony: the test named `..._CannotReachTheDatabase` was flagged during Task 5 as never
actually exercising its catch branch, because no fixture triggered a failure. When the environment
finally produced the real condition, the code did not behave as the requirement intended.

In normal MCP use the blast radius is bounded — `CrystalWorkerClient` kills the worker after 3
minutes — but three minutes of silence on what should be a fast read is poor, and one worker process
is left blocked until then.

**Fix direction:** bound the field-enumeration call in time, not just in exception type. Needs a
design decision about where the bound belongs (worker-side wrapper vs. connection timeout on the
Crystal side).

### PM2 — `ReportRenderer`'s error message misattributes EVERY `COMException` to missing parameters

**Severity: Important. Demonstrably misleading — it misled during this very investigation.**

A genuine file-lock failure surfaced as:

> Could not render "SampleReport.rpt" to PDF: The process cannot access the file because it is being
> used by another process. **Reports that declare parameters with no saved values cannot be
> previewed, because Vibey Reports does not supply parameter values.**

The parameter explanation is unconditionally appended to any `COMException` from `Export`. It was
added in Task 7 (supplement C2) specifically to make the *parameter* case actionable, and it does —
but it now asserts a false cause for every other COM failure. This message reaches Claude through
`preview_report`, so a wrong diagnosis actively misdirects the agent.

**Fix direction:** only append the parameter explanation when the COM message actually indicates
missing parameter values; otherwise pass the COM message through with the report name and no
invented cause.

### PM3 — the worker test suite is flaky under parallel execution

**Severity: Minor (test-only).**

`ExportPdf_ProducesAValidPdfForEveryRenderableFixture("SampleReport.rpt")` failed with *"The process
cannot access the file because it is being used by another process"*, then passed 3/3 when run in
isolation. Multiple test classes open the same fixture `.rpt` concurrently and Crystal's export path
does not tolerate the sharing. xUnit parallelises across collections by default.

**Fix direction:** put the Crystal-touching collections in a single xUnit collection to serialise
them, or give each test its own temp copy of the fixture.

---

## Licensing decision (2026-09-01, user)

**FluentAssertions 8.9.0 is kept deliberately.** From v8 it is governed by the Xceed commercial
licence: free for non-commercial use, paid subscription required for commercial use. The test run
surfaces this as a warning on every suite.

The user's call: this project is personal/non-commercial for now, so the community licence applies.
**If Vibey Reports ever ships commercially, this must be revisited** — either downgrade to
FluentAssertions 7.x (last Apache-2.0 release, near-identical API) or migrate to Shouldly / plain
`Assert.*`.

Note the same version is used by `D:\PHR-X-DB-MCP-SERVER` in both of its test projects, so any future
change likely applies there too.

---

## Sub-report review round 3 (2026-09-01) — the SubreportName/Name identity split

An independent review of `c21005e` found seven issues in `addSubreport`/`setSubreportLink`. All
seven were fixed. The rulings below record the ones that were judgement calls, plus one prediction
in the review that direct measurement contradicted.

### The review's one wrong prediction: there was no silent-corruption path

The review predicted that `setSubreportLink` against a **pre-existing** sub-report would be a
"silent no-op reported as `ok:true`" — i.e. data loss. Measured directly against
`out/reports/PMSV10_GoalAlignCascade.subreport.rpt` (which embeds a sub-report with object
`Name = "Subreport1"` and `SubreportName = "GoalDetail"`), before any fix:

- `target = "Subreport1"` passed the validator, then failed **loudly** in the applier with COM
  `"This value is write-only."`, reported as `ok:false`.
- `target = "GoalDetail"` was rejected by the validator: `Object "GoalDetail" does not exist in the
  report.`

So nothing was ever silently corrupted. The flaw was real but was a **usability/correctness** flaw:
cross-plan linking was impossible to express, and both failure modes named the wrong cause. It has
been fixed as such, and the fix makes cross-plan linking actually work rather than documenting the
limitation away.

### Ruling 1 — a Subreport is addressable by two different names, and both are now reported

`ObjectInfo.Name` is the placed container object's name, which Crystal auto-numbers
(`"Subreport1"`); `ObjectInfo.SubreportName` is the embedded report's own name, which is what
`SubreportController.GetSubreportLinks`/`SetSubreportLinks` — and therefore `setSubreportLink` — are
keyed by. `ReportReader` already read `SubreportName` and threw it away, leaving the agent able to
see only the one name `setSubreportLink` does *not* accept.

**Ruling:** report both. `SubreportName` is now on `ObjectInfo`, in `read_report`'s JSON, and in the
tool description. `LayoutPlanValidator` keeps a second map (`SubreportName -> SimObject`) and
resolves `setSubreportLink`'s target through it, so every other action stays on the object-name
map. **Verified end-to-end** through the real worker against
`PMSV10_GoalAlignCascade.subreport.rpt`: `read_report` reports
`name="Subreport1", subreportName="GoalDetail"`; a plan whose only operation is a
`setSubreportLink` on `"GoalDetail"` applies `ok:true`; the saved report reads back exactly one
link. A second apply against *that* output appends a second link and keeps the first.

`SubreportController.GetSubreportNames()` — present on the SDK surface but unused — is now the
applier's guard: `setSubreportLink`'s target is resolved against it (which also canonicalises
casing, since the validator matches case-insensitively and COM does not), so an unaddressable name
produces a message naming the sub-reports that do exist instead of COM's "This value is write-only."

### Ruling 2 — a sub-report added in the same plan can ONLY be targeted by `setSubreportLink`

`[addSubreport newName="GoalDetail", move target="GoalDetail"]` used to validate clean, apply
operation 0 to the **live** document, then throw at operation 1 from `LayoutApplier.FindObject`
(which matches on the object's own `Name`, and Crystal had named the object `"Subreport1"`). That
faulted the session and lost the entire plan, `addSubreport` included.

Two fixes were open: kind-gate every non-`setSubreportLink` action against `"Subreport"`, or
register the sim entry so only `setSubreportLink` resolves it.

**Ruling: a third, narrower option** — register the added sub-report in **both** maps and flag it
`AddedInPlan`, then reject any non-`setSubreportLink` target operation on it in the shared
`needsTarget` block. This keeps `newName` uniqueness honest against later adds (which a
register-in-one-map-only approach would lose) and, unlike a bare kind gate, does not forbid
`move`/`resize`/`removeObject` on a sub-report that is *already* embedded — those are legitimate and
work, because there the object name is real and known.

The rejection message names the actual cause (Crystal assigns the placed object its own
auto-numbered name at import time) and states that nothing is lost: `ImportSubreportEx` already
receives the geometry at add time, and a second plan can address the object by the name
`read_report` then reports. Verified live: the message is what the worker returns.

### Ruling 3 — `linkedParameter` is now optional, because it has no effect

`docs/sdk-notes.md` records (measured) that Crystal **discards** `LinkedParameterName` and
substitutes its own `{?Pm-<mainReportField>}`. The tool description nevertheless told the agent that
`setSubreportLink` "wires one of the sub-report's parameters to a main-report field".

**Ruling:** requiring a value that provably has no effect only invites the agent to invent a stored
procedure parameter name and believe it was wired up. `linkedParameter` is now optional in the
validator, and the tool description states plainly that Crystal substitutes its own parameter, that
`setSubreportLink` is a **field link only** and cannot target a stored procedure's declared
parameter, and that `subreportField` must be a real sub-report **data** field — parameter forms
(`{?x}`, `{?@x}`, bare `@x`) are all rejected by Crystal with "Invalid field name" (measured).
Verified live: a plan omitting `linkedParameter` entirely applies `ok:true` and reads back the
`{?Pm-...}` substitution.

### Ruling 4 — the `COMException` tolerance must prove it discarded nothing

`SetSubreportLinks` replaces the whole collection. The applier caught **any** `COMException` from
`GetSubreportLinks` and started from a fresh collection, which would turn an append into a replace
and report success. (It also called `links.Add` on a possibly-`null` result, where a
`NullReferenceException` would escape the `COMException`-typed catch.)

**Ruling:** keep the tolerance — a link-less sub-report genuinely may throw rather than return an
empty collection — but make it carry a proof obligation. `null` is folded into the same "could not
read" path, and whenever that path is taken the links are **re-read after the set and required to
number exactly one**. More than one means real links were discarded; a read-back that itself fails
means it cannot be shown they were not. Either way the operation throws, and the session's fault
handling means the report is never saved. Nothing silently succeeds on unproven ground.

### Ruling 5 — `reportPath` absoluteness belongs in the validator, not the applier

The tool contract promised an absolute path and nothing checked it, so a relative path resolved
against the **worker process's** working directory — which the agent cannot see, making even the
applier's "file not found" message name a path the agent never wrote.

**Ruling:** `Path.IsPathRooted` is pure string arithmetic with no file I/O, so it sits beside the
`.rpt` suffix check in the validator without breaking the "validator does no file I/O" rule. The
*existence* check stays in the applier, as before.
