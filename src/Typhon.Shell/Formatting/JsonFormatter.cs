using System.Collections.Generic;
using System.Text.Json;
using Typhon.Shell.Schema;

namespace Typhon.Shell.Formatting;

/// <summary>
/// JSON output format.
/// </summary>
internal sealed class JsonFormatter : IOutputFormatter
{
    public string Name => "json";

    public string FormatEntity(long entityId, string componentName, ComponentSchema schema, IReadOnlyDictionary<string, object> fieldValues)
    {
        var dict = BuildJsonDict(entityId, schema, fieldValues);
        return JsonSerializer.Serialize(dict, JsonOptions);
    }

    public string FormatEntities(string componentName, ComponentSchema schema, IReadOnlyList<(long EntityId, IReadOnlyDictionary<string, object> Fields)> entities)
    {
        var list = new List<Dictionary<string, object>>();

        foreach (var (entityId, fields) in entities)
        {
            list.Add(BuildJsonDict(entityId, schema, fields));
        }

        return JsonSerializer.Serialize(list, JsonOptions);
    }

    private static Dictionary<string, object> BuildJsonDict(long entityId, ComponentSchema schema, IReadOnlyDictionary<string, object> fieldValues)
    {
        var dict = new Dictionary<string, object> { ["entityId"] = entityId };

        foreach (var field in schema.Fields)
        {
            if (fieldValues.TryGetValue(field.Name, out var value))
            {
                dict[field.Name] = value;
            }
        }

        return dict;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };
}
