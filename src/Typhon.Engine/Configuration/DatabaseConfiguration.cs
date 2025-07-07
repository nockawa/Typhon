// unset

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Typhon.Engine;

/// <summary>
/// Provides configuration of the specified type.
/// </summary>
/// <typeparam name="TConfiguration">The configuration type.</typeparam>
// ReSharper disable once TypeParameterCanBeVariant
public interface IConfigurationProvider<TConfiguration>
{
    /// <summary>
    /// Populates the provided configuration object.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    void Configure(TConfiguration configuration);
}

public sealed class DatabaseConfiguration
{
    private string _databaseFileName;

    public string DatabaseName { get; set; }
    public string DatabaseDirectory { get; set; }
    public string DatabaseFileName
    {
        get => _databaseFileName ?? DatabaseName;
        set => _databaseFileName = value;
    }
    public ulong DatabaseCacheSize { get; set; }
    public int WriteCacheSize { get; set; }
    public float WriteThreadRatio { get; set; }
    public bool RecreateDatabase { get; set; }
    public bool DeleteDatabaseOnDispose { get; set; }
    public bool PagesDebugPattern { get; set; }

    public string DatabaseAbsoluteDirectory
    {
        get => Path.GetFullPath(DatabaseDirectory);
    }

    internal bool OverrideDatabaseCacheMinSize { get; set; }

    public bool IsValid => Validate(true, out _);
    internal bool Validate(bool silent, out string validation)
    {
        var sb = new StringBuilder();
        var success = true;

        // DatabaseName
        var singleWordRegEx = new Regex("^[A-Za-z0-9_-]+$");
        if (singleWordRegEx.IsMatch(DatabaseName) == false)
        {
            sb.AppendLine($"Database Name '{DatabaseName}' is invalid");
            success = false;
        }

        if (Encoding.UTF8.GetByteCount(DatabaseName) > 63)
        {
            sb.AppendLine($"Database Name '{DatabaseName}' is too long, must not exceed 63 bytes of its UTF8 version.");
            success = false;
        }

        // DatabaseDirectory
        var absDir = DatabaseAbsoluteDirectory;
        var di = new DirectoryInfo(absDir);
        if (di.Exists == false)
        {
            sb.AppendLine($"Database Directory '{absDir}' does not exist or is not accessible.");
            success = false;
        }

        // DatabaseFilesPrefix
        if (singleWordRegEx.IsMatch(DatabaseFileName) == false)
        {
            sb.AppendLine($"Database Files Prefix '{DatabaseName}' is invalid");
            success = false;
        }

        if (Encoding.UTF8.GetByteCount(DatabaseFileName) > 63)
        {
            sb.AppendLine($"Database Files Prefix'{DatabaseFileName}' is too long, must not exceed 63 bytes of its UTF8 version.");
            success = false;
        }

        // DatabaseCacheSize
        var dcs = DatabaseCacheSize;
        if ((dcs & (PagedMemoryMappedFile.PageSize - 1)) != 0UL)
        {
            sb.AppendLine($"Database Cache Size must be a multiple of the Page Size ('{PagedMemoryMappedFile.PageSize}').");
            success = false;
        }
        if (dcs < PagedMemoryMappedFile.MinimumCacheSize && OverrideDatabaseCacheMinSize==false)
        {
            sb.AppendLine($"Database Cache Size must be at least '{PagedMemoryMappedFile.MinimumCacheSize/(1024*1024)}'MiB.");
            success = false;
        }

        if (dcs > 0x100000000)
        {
            sb.AppendLine($"Database Cache Size is bigger than the current limit of 4GiB");
            success = false;
        }

        // WriteCacheSize
        var wcs = WriteCacheSize;
        if ((wcs & (PagedMemoryMappedFile.WriteCachePageSize-1)) != 0)
        {
            sb.AppendLine($"Database Write Cache Size must be a multiple 1Mib (1024*1024) but is ('{dcs}').");
            success = false;
        }

        // Throw exception if necessary and required
        if (success == false && silent == false)
        {
            throw new Exception(sb.ToString());
        }

        validation = sb.Length==0 ? null : sb.ToString();
        return success;
    }

    internal static bool IsPowerOfTwo(ulong x) => (x & (x - 1)) == 0;
    internal static int FirstSetBitPos(long n) => (int)((Math.Log10(n & -n)) / Math.Log10(2));
}

internal class DefaultDatabaseConfiguration : IConfigurationProvider<DatabaseConfiguration>
{
    internal const ulong DefaultDatabaseFileChunkSize = 1*1024*1024*1024UL;

    public void Configure(DatabaseConfiguration configuration)
    {
        configuration.DatabaseName = "Database";
        configuration.DatabaseDirectory = Directory.GetCurrentDirectory();
        configuration.DatabaseCacheSize = PagedMemoryMappedFile.MinimumCacheSize;
        configuration.WriteCacheSize = 128 * PagedMemoryMappedFile.WriteCachePageSize;
        configuration.WriteThreadRatio = 0.5f;
    }
}