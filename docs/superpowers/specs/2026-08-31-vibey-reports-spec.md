# AI-Assisted Crystal Reports XI R2 Design — Implementation Plan

## Goal

Build a tool that lets AI redesign the visual layout of existing Crystal Reports XI Release 2 `.rpt` files **without controlling the Crystal Reports UI** and without directly editing the proprietary `.rpt` binary format.

The system will use the **Crystal Reports XI R2 In-Process RAS SDK** as the supported interface for reading and modifying report definitions.

---

## Proposed Architecture

```text
Existing .rpt Template
        |
        v
CrystalReportReader
        |
        v
ReportSchema.json
        |
        v
AI Layout Designer
        |
        v
LayoutPlan.json
        |
        v
CrystalReportWriter
        |
        v
Generated .rpt
        |
        v
Optional PDF Preview
        |
        v
AI Visual Review / Refinement
```

---

## Phase 1 — Crystal SDK Proof of Concept

Create a small Windows/.NET Framework application that references the Crystal Reports XI R2 SDK libraries.

Initial objectives:

- Load an existing `.rpt` file.
- Access its `ReportClientDocument`.
- Enumerate:
  - Sections
  - Text objects
  - Database field objects
  - Object positions
  - Object dimensions
- Export this information to JSON.
- Modify the position and size of one existing object.
- Save the modified report as a new `.rpt` file.

Keep this phase completely independent of AI.

---

## Phase 2 — Define the Intermediate JSON Format

Create a simplified JSON representation of the report.

Example:

```json
{
  "page": {
    "width": 11900,
    "height": 16800
  },
  "sections": [
    {
      "name": "Details",
      "height": 400,
      "objects": [
        {
          "id": "CustomerName",
          "type": "field",
          "source": "{Customer.Name}",
          "left": 300,
          "top": 20,
          "width": 2500,
          "height": 300
        }
      ]
    }
  ]
}
```

The AI should work only with this intermediate format rather than Crystal Reports SDK objects.

---

## Phase 3 — Create a Restricted Layout Command Model

Instead of allowing AI to generate arbitrary Crystal Reports code, define a small set of supported operations.

Initially support:

- `MoveObject`
- `ResizeObject`
- `SetFont`
- `SetFontSize`
- `SetBold`
- `SetAlignment`
- `AddText`
- `AddLine`
- `AddBox`
- `ResizeSection`

Example:

```json
{
  "operations": [
    {
      "action": "move",
      "object": "CustomerName",
      "left": 400,
      "top": 50
    }
  ]
}
```

Validate every operation before applying it.

---

## Phase 4 — Build the Crystal Report Writer

Translate the validated layout commands into Crystal RAS SDK calls.

Responsibilities:

- Locate the requested section/object.
- Apply position and size changes.
- Apply formatting changes.
- Add supported visual objects.
- Prevent changes outside the approved design scope.
- Save the result to a new `.rpt`.

The writer should reject changes involving:

- Database connections
- SQL commands
- Formulas
- Parameters
- Record selection
- Business logic

unless those features are explicitly added later.

---

## Phase 5 — Add AI Layout Generation

Send the AI:

- Report schema JSON
- Available fields
- Section information
- Current object layout
- User design instructions

Example instruction:

> Redesign this report to look clean and modern. Preserve all data bindings and formulas. You may only reposition, resize, format, and add decorative layout objects.

Require the AI to return only valid layout commands matching the defined schema.

---

## Phase 6 — Add Automatic Preview and Visual Feedback

After generating the `.rpt`:

1. Render/export the report to PDF.
2. Convert the first few PDF pages to images if needed.
3. Send the preview to the AI for visual review.
4. Ask the AI to identify issues such as:
   - Overlapping fields
   - Poor spacing
   - Misaligned columns
   - Oversized headings
   - Inconsistent margins
5. Generate a revised `LayoutPlan.json`.
6. Apply the revised layout and render again.

Limit the number of automatic refinement passes.

---

## Phase 7 — Separate Legacy Crystal Code

Because Crystal Reports XI R2 is old, keep its SDK isolated in a small legacy process.

Recommended structure:

```text
AIReportDesigner.sln

/src
    /DesignerApp
        Modern UI / API
        .NET 8+

    /CrystalWorker
        .NET Framework
        Crystal XI R2 SDK references

    /SharedContracts
        ReportSchema
        LayoutPlan
        Validation models
```

Communication between the modern application and `CrystalWorker` can use:

- JSON files initially
- Named pipes later
- Local HTTP API if required

This prevents legacy Crystal dependencies from affecting the rest of the application.

---

## Suggested MVP Scope

For the first working version, support only:

- Existing `.rpt` templates
- Text objects
- Database field objects
- Lines
- Boxes
- Position
- Width / height
- Font
- Font size
- Bold
- Alignment
- Section height

Do **not** initially support:

- Charts
- Crosstabs
- Subreports
- Conditional formatting
- Formula creation
- Database changes
- Grouping changes

Add these only after the basic layout workflow is reliable.

---

## MVP Success Criteria

The first milestone is complete when the application can:

1. Load an existing Crystal XI R2 `.rpt`.
2. Extract its layout into JSON.
3. Send that JSON to an AI.
4. Receive a valid layout plan.
5. Apply the layout through the RAS SDK.
6. Save a new `.rpt`.
7. Open the generated `.rpt` successfully in Crystal Reports XI R2.

Once this works reliably, add automatic PDF preview and AI visual refinement.
