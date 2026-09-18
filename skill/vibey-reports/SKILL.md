---
name: vibey-reports
description: Edit Crystal Reports .rpt files - reposition, restyle, recolour, border, add text/fields/page numbers, resize and move between sections, set page breaks, group and sort, embed and link sub-reports, and add, remove or repoint data-source tables. Use when the user asks to change a Crystal report, mentions a .rpt file, or refers to PMSV10 reports. Requires Crystal Reports XI R2 installed on Windows.
---

# Vibey Reports

Edits Crystal Reports `.rpt` files by talking to the Crystal XI R2 SDK from PowerShell.

## How to run it

One JSON request in, one JSON response out. The script relaunches itself as 32-bit automatically.

    echo '{"command":"read","reportPath":"D:/path/report.rpt"}' | powershell -NoProfile -File scripts/vibey.ps1

**Always `read` before you `apply`** - you need the real object names, and reading is free.

`read` returns:

- the page box and margins;
- every section with its height, kind and section-level formatting;
- every object in each section - name, kind, geometry, font, colours, line thickness, border,
  alignment, can-grow, suppression and number format, plus the `dataSource` a field is bound to;
- for a sub-report, its `subreportName` and `subreportLinks` (see below - it is NOT the object name);
- `availableFields` - every data-source field as `formulaForm` (e.g. `{sp_x;1.col}`), its
  `tableAlias` and `valueType`;
- `groups` (field, direction, and the header/footer section names Crystal gave them) and `sorts`.

The object names are what every operation's `target` refers to.

## Applying changes

    {"command":"apply","reportPath":"IN.rpt","outputPath":"OUT.rpt","overwrite":true,
     "plan":{"planVersion":1,"operations":[ ... ]}}

The whole plan is validated first. If any operation fails, **nothing is applied and no output file
is written** - the response lists which operation was rejected and why. The source report is never
modified.

Success is `{"ok":true,"operationsApplied":N,"removedObjects":[...],"removedTables":[...],"schema":{...}}`,
where `schema` is the report **as saved** (its `reportPath` is the output file), so a second `read`
is not needed. Failure is `{"ok":false,"error":"..."}`; a rejected plan adds `validationErrors`, one
entry per bad operation carrying its `operationIndex` and a `message` saying what was wrong.

## Operations

| Group | Actions |
|---|---|
| Geometry | `move` `resize` `resizeSection` `setAlignment` `moveToSection` |
| Adding | `addText` `addLine` `addBox` `addField` `addSpecialField` `removeObject` |
| Type | `setFont` `setFontSize` `setBold` |
| Colour & rules | `setTextColor` `setFillColor` `setLineColor` `setSectionBackground` `setLineThickness` `setBorder` |
| Formatting | `setSectionBreak` `setNumberFormat` `setCanGrow` `setSuppress` |
| Grouping | `addGroup` `addSort` |
| Sub-reports | `addSubreport` `setSubreportLink` |
| Data source | `addTable` `removeTable` `setTableLocation` |

`addSpecialField` takes one of: `pageNumber`, `pageNOfM`, `totalPageCount`, `printDate`,
`printTime`, `reportTitle`, `recordNumber`.

`setAlignment` takes `Left`, `Right`, `Centre` (or `Center`) or `Justified`. Colours are HTML hex,
`#RRGGBB`. All geometry is in twips.

### The newer operations

| Action | Fields |
|---|---|
| `moveToSection` | `target`, `section`, optional `leftTwips` / `topTwips` (default: where it is now) |
| `setLineThickness` | `target` (a Box or Line), `lineThicknessTwips` 0-100 |
| `setBorder` | `target`, any of `left` `right` `top` `bottom` (`none` `single` `double` `dashed` `dotted`), optional `color` |
| `addGroup` | `fieldRef` (a `formulaForm` from `availableFields`), optional `direction` (`ascending`/`descending`), optional `groupIndex` (0 = outermost) |
| `addSort` | `fieldRef`, `direction`, optional `sortIndex` |
| `addSubreport` | `section`, `newName`, `reportPath` (absolute, `.rpt`), `leftTwips` `topTwips` `widthTwips` `heightTwips` |
| `setSubreportLink` | `target` (the **subreportName**), `mainReportField`, `subreportField`, optional `linkedParameter` |
| `addTable` | `target` (an existing table alias to clone the connection from), `tableName` (e.g. `sp_x;1`), `newName` (the new alias) |
| `removeTable` | `target` (a table alias) |
| `setTableLocation` | `target` (a table alias), `tableName` |

## Things that will catch you out

**Always set `fontSizePt` explicitly on anything you add.** A new object inherits the font of the
first fontable object in its section. A caption added to a section whose first object is a 20pt
title comes out at 20pt and is clipped by its own box.

**Grow the section before you place tall content in it.** The validator simulates cumulatively, so
`resizeSection` then `addText` in one plan is fine - but the other order is rejected. Shrinking a
section below an object in it is rejected too.

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

**Number formats only stick on numeric fields.** `setNumberFormat` on a String field reports ok,
but Crystal discards the numeric settings when it saves. Check `valueType` in `availableFields`.

**A border can make its section taller.** Crystal grows the section at save to fit the border, so
the saved report may be a little taller than the plan implied. The `schema` in the response is read
from the saved file, so it shows the real height.

**Borders on drawn objects are limited.** A Line or Box has no `double` style, and a Line keeps only
the side it lies along (`top` when horizontal, `left` when vertical) - the validator rejects the
rest rather than letting Crystal silently drop them. For a rule that grows with a table cell, put the
border on the cell's Text or Field, not on a drawn Line: a Line cannot grow.

**Section coordinates are relative to the section.** `moveToSection` keeps an object's `topTwips`
unless you give a new one, so an object from deep in a tall section lands clipped in a short one.
It keeps everything about the object - including a text object's embedded field - but **the section
it came from can no longer shrink** below where the object was. If you need to shrink that section,
remove the object and re-add it in the new section instead.

**A new group's sections are named for its field.** Grouping on `{sp_x;1.emp_display_number}`
creates `empdisplaynumberHeaderSection1` and `empdisplaynumberFooterSection1`, 250 twips tall. You
can place into them in the same plan; grow them first. A group carries its own sort, so `addSort`
on a grouped field is rejected - use the group's `direction`.

**A sub-report has two names.** The object `read` lists as `Subreport1` is Crystal's container. The
name you gave `addSubreport` becomes its `subreportName`, and that is the only name
`setSubreportLink` accepts. Crystal also never uses the container name you asked for, so an object
operation on a sub-report added in the same plan is rejected - move or resize it in a second plan.

**Sub-report links do not survive every host.** Report Navigator's push mode (`RN_SP = '1'`) never
runs Crystal's link pass, so a linked sub-report fails there with "Missing parameter values". To
**remove** links, remove the sub-report and add it again - never hand Crystal an empty link list,
which hangs it indefinitely.

**`removeTable` refuses while anything is bound to the table.** Crystal itself would silently delete
those fields, so the validator lists them - `removeObject` them earlier in the same plan. Crystal
also refuses a table a formula, record selection, group or sort still uses; those need the Designer.

**`addTable` and `setTableLocation` connect to the database**, and a report keeps its connection's
user name but never its password. Set `VIBEY_DB_PASSWORD` in the environment before running them.
It is never a plan field and never appears in any response.

**After a stored procedure changes**, the report cannot see the new columns: its field list is
cached, and `setTableLocation` does not refresh it. `addTable` does - it reads the columns from the
server - so add the procedure under a new alias, rebuild the fields against that alias, then
`removeTable` the old one. The new table's fields cannot be bound in the same plan that adds it:
apply, `read`, then plan again.

**An empty Report Header still costs a page.** With a page break before a group header, even a
10-twip Report Header prints first and pushes the first group onto page 2. Suppress it
(`setSuppress` on the section); resizing it to 0 is not the same.

**Close the report in the Crystal Designer first.** An open `.rpt` is locked; the tool fails cleanly
and writes nothing, but it cannot save over a file you are looking at.

**Non-ASCII text needs care getting into the request.** Windows PowerShell 5.1 re-encodes a pipe to
a native process with the console code page, which mangles anything outside ASCII on the way in.
Escape non-ASCII characters as `\uXXXX` in the JSON, or write the request to a UTF-8 file and pass
it with `-RequestFile`.

**Crystal Reports 13 on the same machine is fine.** The skill pins the XI R2 engine (11.5.3700.0)
explicitly. It used to load whichever Crystal engine was newest, which broke it outright wherever
the Crystal 13 runtime was also installed.

## What this skill does NOT do

Formulas, record selection, parameters, charts, cross-tabs and pictures are out of scope - use the
Crystal Designer for those.

It also cannot create a `.rpt` from nothing - the SDK has `Open` and `SaveAs` but no `New`. Start
from an existing file, even an empty one.
