using Spectre.Console;
using System.Collections.Generic;
using System.IO;
using Typhon.Schema.Definition;
using Typhon.Shell.Schema;

namespace Typhon.Shell.Formatting;

/// <summary>
/// Bordered table format using Spectre.Console Table widget.
/// </summary>
internal sealed class FullTableFormatter : IOutputFormatter
{
    public string Name => "full-table";

    public string FormatEntity(long entityId, string componentName, ComponentSchema schema, IReadOnlyDictionary<string, object> fieldValues)
    {
        var table = BuildTable(schema);
        AddRow(table, entityId, schema, fieldValues);
        return RenderTable(table);
    }

    public string FormatEntities(string componentName, ComponentSchema schema, IReadOnlyList<(long EntityId, IReadOnlyDictionary<string, object> Fields)> entities)
    {
        var table = BuildTable(schema);

        foreach (var (entityId, fields) in entities)
        {
            AddRow(table, entityId, schema, fields);
        }

        return RenderTable(table) + $"\n  ({entities.Count} result{(entities.Count != 1 ? "s" : "")})";
    }

    private static Table BuildTable(ComponentSchema schema)
    {
        var table = new Table();
        table.AddColumn(new TableColumn("EntityId").RightAligned());

        foreach (var field in schema.Fields)
        {
            var col = new TableColumn(field.Name);
            if (field.Type is FieldType.Int or FieldType.UInt
                or FieldType.Long or FieldType.ULong
                or FieldType.Short or FieldType.UShort
                or FieldType.Float or FieldType.Double
                or FieldType.Byte or FieldType.UByte)
            {
                col.RightAligned();
            }
            table.AddColumn(col);
        }

        return table;
    }

    private static void AddRow(Table table, long entityId, ComponentSchema schema, IReadOnlyDictionary<string, object> fieldValues)
    {
        var cells = new List<string> { entityId.ToString() };

        foreach (var field in schema.Fields)
        {
            if (fieldValues.TryGetValue(field.Name, out var value))
            {
                cells.Add(Markup.Escape(TextToStructConverter.FormatValue(value, field.Type)));
            }
            else
            {
                cells.Add("");
            }
        }

        table.AddRow(cells.ToArray());
    }

    private static string RenderTable(Table table)
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
        console.Write(table);
        return writer.ToString();
    }
}
