using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Typhon.Shell.Commands;
using Typhon.Shell.Schema;
using Typhon.Shell.Session;

namespace Typhon.Shell.Parsing;

/// <summary>
/// Parses .tsh script files. Handles line-by-line commands and block directives (@compact, @json).
/// </summary>
internal sealed class ScriptParser
{
    private readonly ShellSession _session;
    private readonly CommandExecutor _executor;

    public ScriptParser(ShellSession session, CommandExecutor executor)
    {
        _session = session;
        _executor = executor;
    }

    /// <summary>
    /// Executes all commands in a .tsh script file. Halts on first error.
    /// Returns (success, errorMessage).
    /// </summary>
    public (bool Success, string Error) ExecuteFile(string path)
    {
        if (!File.Exists(path))
        {
            return (false, $"Error: Script file not found: {path}");
        }

        var lines = File.ReadAllLines(path);
        return ExecuteLines(lines, path);
    }

    /// <summary>
    /// Executes a sequence of command lines (from pipe or string input).
    /// </summary>
    public (bool Success, string Error) ExecuteLines(string[] lines, string sourceName = "<stdin>")
    {
        var lineNum = 0;
        while (lineNum < lines.Length)
        {
            var line = lines[lineNum].Trim();
            lineNum++;

            // Skip blank lines and comments
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            // Check for block directives
            if (line.StartsWith("@compact", StringComparison.OrdinalIgnoreCase))
            {
                var result = ExecuteCompactBlock(lines, ref lineNum, line, sourceName);
                if (!result.Success)
                {
                    return result;
                }

                continue;
            }

            if (line.StartsWith("@json", StringComparison.OrdinalIgnoreCase))
            {
                var result = ExecuteJsonBlock(lines, ref lineNum, line, sourceName);
                if (!result.Success)
                {
                    return result;
                }

                continue;
            }

            // Regular command
            var cmdResult = _executor.Execute(line);
            WriteOutput(cmdResult);

            if (!cmdResult.Success)
            {
                return (false, $"{sourceName}:{lineNum}: {cmdResult.Output}");
            }

            if (cmdResult.ShouldExit)
            {
                return (true, null);
            }
        }

        return (true, null);
    }

    private (bool Success, string Error) ExecuteCompactBlock(string[] lines, ref int lineNum, string headerLine, string sourceName)
    {
        // Parse: @compact ComponentName
        var parts = headerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return (false, $"{sourceName}:{lineNum}: Syntax error: @compact <ComponentName>");
        }

        var componentName = parts[1];
        if (!_session.ComponentSchemas.TryGetValue(componentName, out var schema))
        {
            var known = _session.ComponentSchemas.Count > 0
                ? string.Join(", ", _session.ComponentSchemas.Keys)
                : "(none)";
            return (false, $"{sourceName}:{lineNum}: Error: Component '{componentName}' not found. Loaded: {known}");
        }

        // Read rows until @end
        while (lineNum < lines.Length)
        {
            var line = lines[lineNum].Trim();
            lineNum++;

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("@end", StringComparison.OrdinalIgnoreCase))
            {
                return (true, null);
            }

            // Parse compact row: positional values matched to schema field order
            var fieldValues = ParseCompactRow(line, schema, out var error);
            if (fieldValues == null)
            {
                return (false, $"{sourceName}:{lineNum}: {error}");
            }

            // Synthesize a create command
            var braceExpr = BuildBraceExpression(fieldValues);
            var cmd = $"create {componentName} {braceExpr}";
            var result = _executor.Execute(cmd);

            WriteOutput(result);

            if (!result.Success)
            {
                return (false, $"{sourceName}:{lineNum}: {result.Output}");
            }
        }

        return (false, $"{sourceName}:{lineNum}: Error: @compact block not terminated with @end");
    }

    private (bool Success, string Error) ExecuteJsonBlock(string[] lines, ref int lineNum, string headerLine, string sourceName)
    {
        // Parse: @json ComponentName
        var parts = headerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return (false, $"{sourceName}:{lineNum}: Syntax error: @json <ComponentName>");
        }

        var componentName = parts[1];
        if (!_session.ComponentSchemas.TryGetValue(componentName, out var schema))
        {
            var known = _session.ComponentSchemas.Count > 0
                ? string.Join(", ", _session.ComponentSchemas.Keys)
                : "(none)";
            return (false, $"{sourceName}:{lineNum}: Error: Component '{componentName}' not found. Loaded: {known}");
        }

        // Collect JSON content until @end
        var jsonBuilder = new StringBuilder();
        var startLine = lineNum;
        while (lineNum < lines.Length)
        {
            var line = lines[lineNum].Trim();
            lineNum++;

            if (line.StartsWith("@end", StringComparison.OrdinalIgnoreCase))
            {
                return ProcessJsonBlock(jsonBuilder.ToString(), componentName, schema, sourceName, startLine);
            }

            jsonBuilder.AppendLine(lines[lineNum - 1]);
        }

        return (false, $"{sourceName}:{lineNum}: Error: @json block not terminated with @end");
    }

    private (bool Success, string Error) ProcessJsonBlock(string json, string componentName, ComponentSchema schema, string sourceName, int startLine)
    {
        json = json.Trim();
        if (string.IsNullOrEmpty(json))
        {
            return (true, null);
        }

        try
        {
            // Auto-detect array vs NDJSON
            if (json.StartsWith('['))
            {
                // JSON array
                var array = JsonSerializer.Deserialize<JsonElement[]>(json);
                foreach (var element in array)
                {
                    var fieldValues = JsonElementToFields(element, schema);
                    var braceExpr = BuildBraceExpression(fieldValues);
                    var result = _executor.Execute($"create {componentName} {braceExpr}");

                    if (!string.IsNullOrEmpty(result.Output))
                    {
                        Console.WriteLine(result.Output);
                    }

                    if (!result.Success)
                    {
                        return (false, $"{sourceName}:{startLine}: {result.Output}");
                    }
                }
            }
            else
            {
                // NDJSON: one object per line
                foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmedLine = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine))
                    {
                        continue;
                    }

                    var element = JsonSerializer.Deserialize<JsonElement>(trimmedLine);
                    var fieldValues = JsonElementToFields(element, schema);
                    var braceExpr = BuildBraceExpression(fieldValues);
                    var result = _executor.Execute($"create {componentName} {braceExpr}");

                    if (!string.IsNullOrEmpty(result.Output))
                    {
                        Console.WriteLine(result.Output);
                    }

                    if (!result.Success)
                    {
                        return (false, $"{sourceName}:{startLine}: {result.Output}");
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            return (false, $"{sourceName}:{startLine}: JSON parse error: {ex.Message}");
        }

        return (true, null);
    }

    private static Dictionary<string, string> ParseCompactRow(string line, ComponentSchema schema, out string error)
    {
        var values = line.Split(',');
        if (values.Length != schema.Fields.Count)
        {
            error = $"Error: Expected {schema.Fields.Count} values (matching schema field count), got {values.Length}";
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < schema.Fields.Count; i++)
        {
            fields[schema.Fields[i].Name] = values[i].Trim();
        }

        error = null;
        return fields;
    }

    private static Dictionary<string, string> JsonElementToFields(JsonElement element, ComponentSchema schema)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in element.EnumerateObject())
        {
            if (schema.FieldsByName.ContainsKey(prop.Name))
            {
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText();
                fields[prop.Name] = value;
            }
        }

        return fields;
    }

    private static void WriteOutput(CommandResult result)
    {
        if (string.IsNullOrEmpty(result.Output))
        {
            return;
        }

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Output)}[/]");
        }
        else if (result.UseMarkup)
        {
            AnsiConsole.MarkupLine(result.Output);
        }
        else
        {
            Console.WriteLine(result.Output);
        }
    }

    private static string BuildBraceExpression(Dictionary<string, string> fields)
    {
        var sb = new StringBuilder("{ ");
        var first = true;
        foreach (var kvp in fields)
        {
            if (!first)
            {
                sb.Append(", ");
            }

            sb.Append(kvp.Key);
            sb.Append('=');

            // Check if value needs quoting (strings)
            if (kvp.Value.Contains(' ') || kvp.Value.StartsWith('"'))
            {
                if (!kvp.Value.StartsWith('"'))
                {
                    sb.Append('"');
                    sb.Append(kvp.Value);
                    sb.Append('"');
                }
                else
                {
                    sb.Append(kvp.Value);
                }
            }
            else
            {
                sb.Append(kvp.Value);
            }

            first = false;
        }

        sb.Append(" }");
        return sb.ToString();
    }
}
