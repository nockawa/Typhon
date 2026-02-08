using System.Collections.Generic;

namespace Typhon.Benchmark.Cli;

/// <summary>
/// Settings for configuring benchmark execution.
/// </summary>
public class BenchmarkSettings
{
    /// <summary>
    /// Number of warmup iterations before actual measurement.
    /// </summary>
    public int WarmupCount { get; set; } = 1;

    /// <summary>
    /// Number of benchmark iterations to run.
    /// </summary>
    public int IterationCount { get; set; } = 1;

    /// <summary>
    /// Export results to JSON format (always enabled by BenchmarkDotNet).
    /// </summary>
    public bool ExportJson { get; set; } = true;

    /// <summary>
    /// Export results to Markdown format.
    /// </summary>
    public bool ExportMarkdown { get; set; } = false;

    /// <summary>
    /// Export results to HTML format.
    /// </summary>
    public bool ExportHtml { get; set; } = false;

    /// <summary>
    /// Export results with R plots.
    /// </summary>
    public bool ExportRPlot { get; set; } = false;

    /// <summary>
    /// Open HTML/RPlot results in browser after benchmark completion.
    /// </summary>
    public bool OpenResultsInBrowser { get; set; } = false;

    /// <summary>
    /// Creates command-line arguments for BenchmarkDotNet based on current settings.
    /// </summary>
    public string[] ToArgs(string filter = null)
    {
        var args = new List<string>();

        if (!string.IsNullOrEmpty(filter))
        {
            args.Add("--filter");
            args.Add($"*{filter}*");
        }

        // Exporters
        var exporters = new List<string>();
        if (ExportJson) exporters.Add("json");
        if (ExportMarkdown) exporters.Add("markdown");
        if (ExportHtml) exporters.Add("html");
        if (ExportRPlot) exporters.Add("rplot");

        if (exporters.Count > 0)
        {
            args.Add("--exporters");
            args.Add(string.Join(",", exporters));
        }

        return args.ToArray();
    }
}
