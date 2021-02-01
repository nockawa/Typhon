// unset

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Typhon.Engine
{
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
        public bool DeleteDatabaseOnDispose { get; set; }

        public string DatabaseAbsoluteDirectory
        {
            get => Path.GetFullPath(DatabaseDirectory);
        }
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

            // DatabaseVirtualSize
            var dcs = DatabaseCacheSize;
            if ((dcs & (VirtualDiskManager.PageSize - 1)) != 0UL)
            {
                sb.AppendLine($"Database Cache Size must be a multiple of the Page Size ('{VirtualDiskManager.PageSize}').");
                success = false;
            }
            if (dcs < VirtualDiskManager.MinimumCacheSize)
            {
                sb.AppendLine($"Database Cache Size must be at least '{VirtualDiskManager.MinimumCacheSize/(1024*1024)}'MiB.");
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
            configuration.DatabaseCacheSize = VirtualDiskManager.MinimumCacheSize;
        }
    }
}