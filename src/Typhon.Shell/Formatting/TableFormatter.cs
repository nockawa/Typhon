using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Typhon.Shell.Schema;

namespace Typhon.Shell.Formatting;

/// <summary>
/// Default compact table format: Entity N | Field=Value  Field=Value
/// </summary>
internal sealed class TableFormatter : IOutputFormatter
{
    public string Name => "table";

    public string FormatEntity(long entityId, string componentName, ComponentSchema schema, IReadOnlyDictionary<string, object> fieldValues)
    {
        var sb = new StringBuilder();
        sb.Append($"  Entity {entityId} |");

        foreach (var field in schema.Fields)
        {
            if (fieldValues.TryGetValue(field.Name, out var value))
            {
                sb.Append($" {field.Name}={TextToStructConverter.FormatValue(value, field.Type)}");
            }
        }

        return sb.ToString();
    }

    public string FormatEntities(string componentName, ComponentSchema schema, IReadOnlyList<(long EntityId, IReadOnlyDictionary<string, object> Fields)> entities)
    {
        var sb = new StringBuilder();

        foreach (var (entityId, fields) in entities)
        {
            sb.AppendLine(FormatEntity(entityId, componentName, schema, fields));
        }

        sb.Append($"  ({entities.Count} result{(entities.Count != 1 ? "s" : "")})");
        return sb.ToString();
    }
}
