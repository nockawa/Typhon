using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typhon.Workbench.Streams;

/// <summary>
/// JSON serializer options shared by every SSE stream in the Workbench profiler pipeline. The MVC
/// pipeline's JSON options (configured in <c>ServiceExtensions</c>) apply only to controller action
/// results; direct <see cref="JsonSerializer.Serialize"/> calls ignore that configuration and fall
/// back to defaults (PascalCase). Every SSE writer must go through <see cref="Web"/> so clients see
/// a consistent camelCase wire format.
/// </summary>
internal static class SseJsonOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
