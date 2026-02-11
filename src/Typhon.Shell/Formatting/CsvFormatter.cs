using System.Collections.Generic;
using System.Text;
using Typhon.Shell.Schema;

namespace Typhon.Shell.Formatting;

/// <summary>
/// CSV output format with header row.
/// </summary>
internal sealed class CsvFormatter : IOutputFormatter
{
    public string Name => "csv";

    public string FormatEntity(long entityId, string componentName, ComponentSchema schema, IReadOnlyDictionary<string, object> fieldValues)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildHeader(schema));
        sb.Append(BuildRow(entityId, schema, fieldValues));
        return sb.ToString();
    }

    public string FormatEntities(string componentName, ComponentSchema schema, IReadOnlyList<(long EntityId, IReadOnlyDictionary<string, object> Fields)> entities)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildHeader(schema));

        foreach (var (entityId, fields) in entities)
        {
            sb.AppendLine(BuildRow(entityId, schema, fields));
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildHeader(ComponentSchema schema)
    {
        var sb = new StringBuilder("EntityId");

        foreach (var field in schema.Fields)
        {
            sb.Append(',');
            sb.Append(field.Name);
        }

        return sb.ToString();
    }

    private static string BuildRow(long entityId, ComponentSchema schema, IReadOnlyDictionary<string, object> fieldValues)
    {
        var sb = new StringBuilder(entityId.ToString());

        foreach (var field in schema.Fields)
        {
            sb.Append(',');
            if (fieldValues.TryGetValue(field.Name, out var value))
            {
                var formatted = TextToStructConverter.FormatValue(value, field.Type);
                // CSV-escape strings containing commas or quotes
                if (formatted.Contains(',') || formatted.Contains('"'))
                {
                    sb.Append('"');
                    sb.Append(formatted.Replace("\"", "\"\""));
                    sb.Append('"');
                }
                else
                {
                    sb.Append(formatted);
                }
            }
        }

        return sb.ToString();
    }
}
