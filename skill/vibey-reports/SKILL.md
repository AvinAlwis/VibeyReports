---
name: vibey-reports
description: Edit the layout of Crystal Reports .rpt files - reposition, restyle, recolour, add text/fields/page numbers, resize sections, set page breaks. Use when the user asks to change how a Crystal report looks, mentions a .rpt file, or refers to PMSV10 reports. Requires Crystal Reports XI R2 installed on Windows.
---

# Vibey Reports

Edits Crystal Reports `.rpt` layouts by talking to the Crystal XI R2 SDK from PowerShell.

## How to run it

One JSON request in, one JSON response out. The script relaunches itself as 32-bit automatically.

    echo '{"command":"read","reportPath":"D:/path/report.rpt"}' | powershell -NoProfile -File scripts/vibey.ps1

**Always `read` before you `apply`** - you need the real object names, and reading is free.

`read` returns the page box and margins, every section with its height, and every object in each
section with its name, kind, geometry, font, colours and number format. Those names are what every
operation's `target` refers to.

## Applying changes

    {"command":"apply","reportPath":"IN.rpt","outputPath":"OUT.rpt","overwrite":true,
     "plan":{"planVersion":1,"operations":[ ... ]}}

The whole plan is validated first. If any operation fails, **nothing is applied and no output file
is written** - the response lists which operation was rejected and why. The source report is never
modified.

Success is `{"ok":true,"operationsApplied":N,"removedObjects":[...],"schema":{...}}`, where `schema`
is the report **as saved** (its `reportPath` is the output file), so a second `read` is not needed.
Failure is `{"ok":false,"error":"..."}`; a rejected plan adds `validationErrors`, one entry per bad
operation carrying its `operationIndex` and a `message` saying what was wrong.

## Operations

| Group | Actions |
|---|---|
| Geometry | `move` `resize` `resizeSection` `setAlignment` |
| Adding | `addText` `addLine` `addBox` `addField` `addSpecialField` `removeObject` |
| Type | `setFont` `setFontSize` `setBold` |
| Colour | `setTextColor` `setFillColor` `setLineColor` `setSectionBackground` |
| Formatting | `setSectionBreak` `setNumberFormat` `setCanGrow` `setSuppress` |

`addSpecialField` takes one of: `pageNumber`, `pageNOfM`, `totalPageCount`, `printDate`,
`printTime`, `reportTitle`, `recordNumber`.

`setAlignment` takes `Left`, `Right`, `Centre` (or `Center`) or `Justified`. Colours are HTML hex,
`#RRGGBB`.

## Things that will catch you out

**Always set `fontSizePt` explicitly on anything you add.** A new object inherits the font of the
first fontable object in its section. A caption added to a section whose first object is a 20pt
title comes out at 20pt and is clipped by its own box.

**Grow the section before you place tall content in it.** The validator simulates cumulatively, so
`resizeSection` then `addText` in one plan is fine - but the other order is rejected.

**A section cannot exceed one printable page** (16118 twips on A4 with default margins - the limit
is computed from the report's own page size and margins, not hardcoded). Two pages of content need
two sections.

**Twips, not points or pixels.** 1440 twips = 1 inch. A4 is 11906 x 16838; printable width with
default margins is 11186.

**Objects do not reflow.** Growing one object does not push down the ones below it in the same
section - they will overlap. Put things that grow in separate sections.

**A `Line` carries no font and no fill**, and a `Box` carries no font. `setBold` on a Line and
`setFillColor` on a Line are both rejected rather than silently ignored. Lines must be horizontal or
vertical; a diagonal is rejected.

**Close the report in the Crystal Designer first.** An open `.rpt` is locked; the tool fails cleanly
and writes nothing, but it cannot save over a file you are looking at.

**After a stored procedure changes**, the report cannot see new columns until someone runs
*Database > Verify Database* in the Designer - including inside any embedded sub-report. There is no
SDK equivalent. Skip it and the next operation naming a new column fails with `Invalid field name`,
which reads like a typo but is not.

**Non-ASCII text needs care getting into the request.** Windows PowerShell 5.1 re-encodes a pipe to
a native process with the console code page, which mangles anything outside ASCII on the way in.
Escape non-ASCII characters as `\uXXXX` in the JSON, or write the request to a UTF-8 file and pass
it with `-RequestFile`.

## What this skill does NOT do

Data sources (`addTable`, `removeTable`, `setTableLocation`), sub-reports, groups, sorts, formulas
and record selection are all out of scope. `removeTable` in particular is dangerous: Crystal does not
refuse to remove a table that fields are bound to - it silently deletes them. Use the Crystal
Designer for those.

It also cannot create a `.rpt` from nothing - the SDK has `Open` and `SaveAs` but no `New`. Start
from an existing file, even an empty one.
