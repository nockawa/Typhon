using JetBrains.Annotations;
using System.Collections.Generic;
using Typhon.Shell.Schema;

namespace Typhon.Shell.Formatting;

/// <summary>
/// Renders entity data in a specific output format (table, full-table, JSON, CSV).
/// </summary>
[PublicAPI]
internal interface IOutputFormatter
{
    string Name { get; }

    /// <summary>
    /// Formats a single entity's data.
    /// </summary>
    string FormatEntity(long entityId, string componentName, ComponentSchema schema, IReadOnlyDictionary<string, object> fieldValues);

    /// <summary>
    /// Formats multiple entities' data.
    /// </summary>
    string FormatEntities(string componentName, ComponentSchema schema, IReadOnlyList<(long EntityId, IReadOnlyDictionary<string, object> Fields)> entities);
}
