## Amendment to Task 5 — also extract the available field list

Task 5's `ReportReader` gains one more responsibility. The spec's Phase 5 says the AI must be sent
"Available fields"; the original plan dropped that, which is precisely why `addField` was
unreachable. Restore it.

**Additional files:** none — extend `src/VibeyReports.Contracts/ReportSchema.cs` and
`src/VibeyReports.CrystalWorker/ReportReader.cs`.

**Additional interface produced:** `ReportSchema.AvailableFields` (`List<FieldInfo>`), and:

```csharp
public sealed class FieldInfo
{
    /// <summary>Raw field name, e.g. "stage_name".</summary>
    public string Name { get; set; } = "";
    /// <summary>The bindable expression, e.g. "{Command.stage_name}". Use THIS as addField's fieldRef.</summary>
    public string FormulaForm { get; set; } = "";
    public string TableAlias { get; set; } = "";
    /// <summary>String, Number, Currency, DateTime, Date, Time, Boolean, Blob, Other.</summary>
    public string ValueType { get; set; } = "";
    /// <summary>Crystal's default heading for this field, useful as addText content.</summary>
    public string HeadingText { get; set; } = "";
}
```

Extraction, inside `ReportReader.Read`, after the sections loop:

```csharp
var tables = doc.DatabaseController.Database.Tables;
for (var t = 0; t < tables.Count; t++)
{
    var table = tables[t];
    var fields = table.DataFields;
    for (var f = 0; f < fields.Count; f++)
    {
        var dbField = (ISCRDBField)fields[f];
        schema.AvailableFields.Add(new FieldInfo
        {
            Name = dbField.Name ?? "",
            FormulaForm = dbField.FormulaForm ?? "",
            TableAlias = dbField.TableAlias ?? "",
            ValueType = ClassifyValueType(dbField.Type),
            HeadingText = dbField.HeadingText ?? ""
        });
    }
}
```

with:

```csharp
private static string ClassifyValueType(CrFieldValueTypeEnum type)
{
    switch (type)
    {
        case CrFieldValueTypeEnum.crFieldValueTypeStringField: return "String";
        case CrFieldValueTypeEnum.crFieldValueTypeInt8sField:
        case CrFieldValueTypeEnum.crFieldValueTypeInt8uField:
        case CrFieldValueTypeEnum.crFieldValueTypeInt16sField:
        case CrFieldValueTypeEnum.crFieldValueTypeInt16uField:
        case CrFieldValueTypeEnum.crFieldValueTypeInt32sField:
        case CrFieldValueTypeEnum.crFieldValueTypeInt32uField:
        case CrFieldValueTypeEnum.crFieldValueTypeNumberField: return "Number";
        case CrFieldValueTypeEnum.crFieldValueTypeCurrencyField: return "Currency";
        case CrFieldValueTypeEnum.crFieldValueTypeDateField: return "Date";
        case CrFieldValueTypeEnum.crFieldValueTypeTimeField: return "Time";
        case CrFieldValueTypeEnum.crFieldValueTypeDateTimeField: return "DateTime";
        case CrFieldValueTypeEnum.crFieldValueTypeBooleanField: return "Boolean";
        case CrFieldValueTypeEnum.crFieldValueTypeBlobField: return "Blob";
        default: return "Other";
    }
}
```

The exact `CrFieldValueTypeEnum` member names must be confirmed against the installed assembly; if
a member above does not exist, keep the mapping shape and drop or rename that case. The reader must
compile against the real enum, not this listing.

**Additional tests for Task 5:**

```csharp
[Fact]
public void Read_ReturnsAvailableFieldsWithBindableFormulaForms()
{
    using var session = CrystalSession.Open(Fixtures.SampleReport);

    var schema = ReportReader.Read(session);

    schema.AvailableFields.Should().NotBeEmpty();
    schema.AvailableFields.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Name));
    schema.AvailableFields.Should().OnlyContain(f => f.FormulaForm.StartsWith("{") && f.FormulaForm.EndsWith("}"));
}

[Fact]
public void Read_AvailableFieldsCoverTheDataSourcesOfPlacedFieldObjects()
{
    using var session = CrystalSession.Open(Fixtures.SampleReport);

    var schema = ReportReader.Read(session);
    var placed = schema.Sections.SelectMany(s => s.Objects)
                       .Where(o => o.Kind == "Field" && !string.IsNullOrWhiteSpace(o.DataSource))
                       .Select(o => o.DataSource!)
                       .ToList();

    // Every already-placed database field should be referenceable by addField.
    // Formula and special fields legitimately are not, so this asserts overlap, not containment.
    if (placed.Count > 0)
    {
        placed.Any(p => schema.AvailableFields.Any(f => f.FormulaForm == p))
              .Should().BeTrue();
    }
}
```

**Note for the implementer:** enumerating tables must not trigger a database logon. If it does on a
fixture, catch the failure and leave `AvailableFields` empty for that report rather than failing the
read — reading layout must never require a live connection. Add a test asserting `Read` still
succeeds in that case.

---

