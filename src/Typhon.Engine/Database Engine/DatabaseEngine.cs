using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System;

[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.Collections.Benchmark")]

namespace Typhon.Engine
{
    public partial class DatabaseEngine : IInitializable, IDisposable
    {
        private readonly DatabaseConfiguration     _dbc;
        private readonly VirtualDiskManager        _vdm;
        private readonly LogicalSegmentManager     _lsm;
        private readonly DiskPageAllocator         _dpa;
        private readonly ILogger<DatabaseEngine>   _log;

        private DatabaseDefinitions _databaseDefinitions;

        public LogicalSegmentManager LSM => _lsm;

        public DatabaseEngine(IConfiguration<DatabaseConfiguration> dbc, VirtualDiskManager vdm, LogicalSegmentManager lsm, DiskPageAllocator dpa, ILogger<DatabaseEngine> log)
        {
            _vdm = vdm;
            _lsm = lsm;
            _dpa = dpa;
            _log = log;
            _dbc = dbc.Value;

            _databaseDefinitions = new DatabaseDefinitions();
            ConstructComponentStore();

            // Check the configuration
            _dbc.Validate(false, out _);

            _vdm.DatabaseCreating += OnDatabaseCreating;
            _vdm.DatabaseLoading += OnDatabaseLoading;

        }

        private void OnDatabaseLoading(object sender, DatabaseEventArgs e)
        {
        }

        unsafe private void OnDatabaseCreating(object sender, DatabaseEventArgs e)
        {
            CreateComponentStore(e.Header);
        }

        public void Initialize()
        {
            ++ReferenceCounter;
            if (IsInitialized)
            {
                return;
            }
            _vdm.Initialize();
            _lsm.Initialize();
            _dpa.Initialize();

            IsInitialized = true;
            return;
        }
        public bool IsInitialized { get; private set; }
        public bool IsDisposed { get; private set; }
        public int ReferenceCounter { get; private set; }

        public void Dispose()
        {
            if (IsDisposed || --ReferenceCounter!=0)
            {
                return;
            }

            _dpa.Dispose();
            _lsm.Dispose();
            _vdm.Dispose();

            IsDisposed = true;
        }
    }
}
