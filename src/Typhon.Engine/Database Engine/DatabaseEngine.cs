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
        private readonly DatabaseDefinitions       _dbd;

        public LogicalSegmentManager LSM => _lsm;
        public DatabaseDefinitions DBD => _dbd;

        /// <summary>
        /// Create a transaction in order to make Queries and CRUD operation on the database
        /// </summary>
        /// <param name="exclusiveConcurrency">If <c>true</c> the write accesses on the Components will be exclusive: any updated, deleted Components
        /// will be locked for the rest of the transaction, preventing other transactions in other threads to modify them as well.
        /// If <c>false</c> the transaction is running in optimistic concurrency mode, allowing concurrent changes across transactions with possible
        /// conflicts being resolved during commit time.
        /// </param>
        /// <returns>The transaction object</returns>
        /// <remarks>
        /// Typhon deals with accesses and changes through transaction only, even for query purpose. When the user creates a transaction, "now" (the
        /// time when the transaction was created) is used as the reference point, every access will be based on the data that existed up to this point.
        /// Every changes will be isolated from other transactions until the content is committed.
        /// </remarks>
        public Transaction NewTransaction(bool exclusiveConcurrency)
        {
            return new Transaction(this, exclusiveConcurrency);
        }

        public DatabaseEngine(IConfiguration<DatabaseConfiguration> dbc, VirtualDiskManager vdm, LogicalSegmentManager lsm, DiskPageAllocator dpa, ILogger<DatabaseEngine> log)
        {
            _vdm = vdm;
            _lsm = lsm;
            _dpa = dpa;
            _log = log;
            _dbc = dbc.Value;

            _dbd = new DatabaseDefinitions();
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
