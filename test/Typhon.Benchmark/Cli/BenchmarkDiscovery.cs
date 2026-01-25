using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace Typhon.Benchmark.Cli;

/// <summary>
/// Represents a discovered benchmark class with its metadata.
/// </summary>
public record BenchmarkInfo(
    Type Type,
    string Name,
    string Category,
    int MethodCount
)
{
    public override string ToString() => Name;
}

/// <summary>
/// Discovers benchmark classes in the assembly using reflection.
/// </summary>
public static class BenchmarkDiscovery
{
    public const string DefaultCategory = "General";

    /// <summary>
    /// Discovers all benchmark classes in the specified assembly.
    /// </summary>
    public static IReadOnlyList<BenchmarkInfo> DiscoverBenchmarks(Assembly assembly)
    {
        var benchmarks = new List<BenchmarkInfo>();

        foreach (var type in assembly.GetTypes())
        {
            // Skip non-public, abstract, or nested types
            if (!type.IsPublic || type.IsAbstract || type.IsNested)
                continue;

            // Check if the type has any methods with [Benchmark] attribute
            var benchmarkMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<BenchmarkAttribute>() != null)
                .ToList();

            if (benchmarkMethods.Count == 0)
                continue;

            // Get category from [BenchmarkCategory] attribute
            var categoryAttr = type.GetCustomAttribute<BenchmarkCategoryAttribute>();
            var category = categoryAttr?.Categories.FirstOrDefault() ?? DefaultCategory;

            // Clean up the name (remove "Benchmark" suffix for display)
            var displayName = type.Name;
            if (displayName.EndsWith("Benchmark", StringComparison.OrdinalIgnoreCase))
                displayName = displayName[..^9];
            if (displayName.EndsWith("Bench", StringComparison.OrdinalIgnoreCase))
                displayName = displayName[..^5];

            benchmarks.Add(new BenchmarkInfo(
                Type: type,
                Name: displayName,
                Category: category,
                MethodCount: benchmarkMethods.Count
            ));
        }

        return benchmarks.OrderBy(b => b.Category).ThenBy(b => b.Name).ToList();
    }

    /// <summary>
    /// Groups benchmarks by their category.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<BenchmarkInfo>> GroupByCategory(
        IEnumerable<BenchmarkInfo> benchmarks)
    {
        return benchmarks
            .GroupBy(b => b.Category)
            .OrderBy(g => g.Key == DefaultCategory ? "zzz" : g.Key) // Put "General" last
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<BenchmarkInfo>)g.OrderBy(b => b.Name).ToList()
            );
    }

    /// <summary>
    /// Gets all unique categories from the benchmarks.
    /// </summary>
    public static IReadOnlyList<string> GetCategories(IEnumerable<BenchmarkInfo> benchmarks)
    {
        return benchmarks
            .Select(b => b.Category)
            .Distinct()
            .OrderBy(c => c == DefaultCategory ? "zzz" : c)
            .ToList();
    }
}
