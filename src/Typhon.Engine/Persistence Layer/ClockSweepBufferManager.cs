using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Represents a page in the buffer pool with metadata for the Clock algorithm
/// </summary>
public class BufferPage
{
    public int PageId { get; set; }
    public byte[] Data { get; set; }
    public bool IsDirty { get; set; }
    public int UsageCount { get; set; }
    public int PinCount { get; set; }
    public bool IsValid { get; set; }
    public DateTime LastAccessed { get; set; }

    // Lock for thread-safe access to page metadata
    private readonly object _lock = new object();

    public BufferPage(int pageId, int pageSize = 8192)
    {
        PageId = pageId;
        Data = new byte[pageSize];
        IsDirty = false;
        UsageCount = 0;
        PinCount = 0;
        IsValid = false;
        LastAccessed = DateTime.Now;
    }

    /// <summary>
    /// Safely increment usage count up to maximum
    /// </summary>
    public void IncrementUsageCount()
    {
        lock (_lock)
        {
            if (UsageCount < ClockSweepBufferManager.BM_MAX_USAGE_COUNT)
            {
                UsageCount++;
            }
            LastAccessed = DateTime.Now;
        }
    }

    /// <summary>
    /// Safely decrement usage count down to zero
    /// </summary>
    public void DecrementUsageCount()
    {
        lock (_lock)
        {
            if (UsageCount > 0)
            {
                UsageCount--;
            }
        }
    }

    /// <summary>
    /// Pin the page (increment pin count)
    /// </summary>
    public void Pin()
    {
        lock (_lock)
        {
            PinCount++;
            IncrementUsageCount();
        }
    }

    /// <summary>
    /// Unpin the page (decrement pin count)
    /// </summary>
    public void Unpin()
    {
        lock (_lock)
        {
            if (PinCount > 0)
            {
                PinCount--;
            }
        }
    }

    /// <summary>
    /// Check if page is pinned (thread-safe)
    /// </summary>
    public bool IsPinned()
    {
        lock (_lock)
        {
            return PinCount > 0;
        }
    }
}

/// <summary>
/// Clock-Sweep Buffer Manager implementation with usage counters
/// Based on PostgreSQL's clock-sweep algorithm
/// </summary>
public class ClockSweepBufferManager
{
    // Maximum usage count (similar to PostgreSQL's BM_MAX_USAGE_COUNT)
    public const int BM_MAX_USAGE_COUNT = 5;

    private readonly BufferPage[] _bufferPool;
    private readonly Dictionary<int, int> _pageMap; // PageId -> BufferIndex
    private readonly int _bufferSize;
    private int _clockHand; // Current position of clock hand
    private readonly object _clockLock = new object();
    private readonly ReaderWriterLockSlim _pageMapLock = new ReaderWriterLockSlim();

    // Statistics
    private long _pageHits = 0;
    private long _pageMisses = 0;
    private long _evictions = 0;

    public ClockSweepBufferManager(int bufferSize)
    {
        _bufferSize = bufferSize;
        _bufferPool = new BufferPage[bufferSize];
        _pageMap = new Dictionary<int, int>();
        _clockHand = 0;

        // Initialize buffer pool
        for (int i = 0; i < bufferSize; i++)
        {
            _bufferPool[i] = new BufferPage(-1); // -1 indicates empty slot
        }
    }

    /// <summary>
    /// Access a page, implementing the Clock algorithm logic
    /// </summary>
    public BufferPage AccessPage(int pageId)
    {
        _pageMapLock.EnterReadLock();
        try
        {
            // Check if page is already in buffer (cache hit)
            if (_pageMap.TryGetValue(pageId, out int bufferIndex))
            {
                var page = _bufferPool[bufferIndex];
                page.IncrementUsageCount();
                page.Pin();
                Interlocked.Increment(ref _pageHits);
                return page;
            }
        }
        finally
        {
            _pageMapLock.ExitReadLock();
        }

        // Page not in buffer - need to load it (cache miss)
        Interlocked.Increment(ref _pageMisses);
        return LoadPageIntoBuffer(pageId);
    }

    /// <summary>
    /// Load a page into buffer, potentially evicting another page
    /// </summary>
    private BufferPage LoadPageIntoBuffer(int pageId)
    {
        int victimIndex = FindVictimPage();
        BufferPage victimPage = _bufferPool[victimIndex];

        _pageMapLock.EnterWriteLock();
        try
        {
            // Remove old page from map if it exists
            if (victimPage.IsValid && victimPage.PageId != -1)
            {
                _pageMap.Remove(victimPage.PageId);

                // If page is dirty, write it to disk (simulate)
                if (victimPage.IsDirty)
                {
                    WritePageToDisk(victimPage);
                }

                Interlocked.Increment(ref _evictions);
            }

            // Load new page (simulate reading from disk)
            victimPage.PageId = pageId;
            victimPage.IsValid = true;
            victimPage.IsDirty = false;
            victimPage.UsageCount = 1; // New pages start with usage count 1
            victimPage.PinCount = 1;   // Pin the page for the requester
            victimPage.LastAccessed = DateTime.Now;

            // Simulate loading data from disk
            LoadPageFromDisk(victimPage);

            // Add to page map
            _pageMap[pageId] = victimIndex;

            return victimPage;
        }
        finally
        {
            _pageMapLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Find a victim page using the Clock algorithm with usage counters
    /// </summary>
    private int FindVictimPage()
    {
        lock (_clockLock)
        {
            int attempts = 0;
            int maxAttempts = _bufferSize * 2; // Prevent infinite loop

            while (attempts < maxAttempts)
            {
                BufferPage currentPage = _bufferPool[_clockHand];

                // Skip pinned pages
                if (currentPage.IsPinned())
                {
                    AdvanceClockHand();
                    attempts++;
                    continue;
                }

                // If page is invalid or empty, use it immediately
                if (!currentPage.IsValid || currentPage.PageId == -1)
                {
                    int victimIndex = _clockHand;
                    AdvanceClockHand();
                    return victimIndex;
                }

                // If usage count is 0, this page is a victim
                if (currentPage.UsageCount == 0)
                {
                    int victimIndex = _clockHand;
                    AdvanceClockHand();
                    return victimIndex;
                }

                // Decrement usage count and continue
                currentPage.DecrementUsageCount();
                AdvanceClockHand();
                attempts++;
            }

            // Emergency fallback - return current position
            // This should rarely happen in practice
            return _clockHand;
        }
    }

    /// <summary>
    /// Advance the clock hand to the next position
    /// </summary>
    private void AdvanceClockHand()
    {
        _clockHand = (_clockHand + 1) % _bufferSize;
    }

    /// <summary>
    /// Unpin a page when no longer needed
    /// </summary>
    public void UnpinPage(int pageId, bool markDirty = false)
    {
        _pageMapLock.EnterReadLock();
        try
        {
            if (_pageMap.TryGetValue(pageId, out int bufferIndex))
            {
                BufferPage page = _bufferPool[bufferIndex];
                page.Unpin();

                if (markDirty)
                {
                    page.IsDirty = true;
                }
            }
        }
        finally
        {
            _pageMapLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Simulate writing a page to disk
    /// </summary>
    private void WritePageToDisk(BufferPage page)
    {
        // Simulate disk write operation
        Thread.Sleep(1); // Simulate I/O delay
        Console.WriteLine($"Writing page {page.PageId} to disk");
    }

    /// <summary>
    /// Simulate loading a page from disk
    /// </summary>
    private void LoadPageFromDisk(BufferPage page)
    {
        // Simulate disk read operation
        Thread.Sleep(1); // Simulate I/O delay
        Console.WriteLine($"Loading page {page.PageId} from disk");

        // Simulate filling page with data
        var random = new Random();
        random.NextBytes(page.Data);
    }

    /// <summary>
    /// Get buffer statistics
    /// </summary>
    public BufferStats GetStats()
    {
        _pageMapLock.EnterReadLock();
        try
        {
            return new BufferStats
            {
                TotalPages = _pageMap.Count,
                PageHits = _pageHits,
                PageMisses = _pageMisses,
                Evictions = _evictions,
                HitRatio = _pageHits + _pageMisses > 0 ? 
                    (double)_pageHits / (_pageHits + _pageMisses) : 0.0
            };
        }
        finally
        {
            _pageMapLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get usage count distribution for analysis
    /// </summary>
    public Dictionary<int, int> GetUsageCountDistribution()
    {
        var distribution = new Dictionary<int, int>();

        for (int i = 0; i <= BM_MAX_USAGE_COUNT; i++)
        {
            distribution[i] = 0;
        }

        lock (_clockLock)
        {
            for (int i = 0; i < _bufferSize; i++)
            {
                if (_bufferPool[i].IsValid)
                {
                    distribution[_bufferPool[i].UsageCount]++;
                }
            }
        }

        return distribution;
    }

    /// <summary>
    /// Force eviction of all unpinned pages (for testing)
    /// </summary>
    public void FlushAllPages()
    {
        _pageMapLock.EnterWriteLock();
        try
        {
            for (int i = 0; i < _bufferSize; i++)
            {
                BufferPage page = _bufferPool[i];
                if (page.IsValid && !page.IsPinned())
                {
                    if (page.IsDirty)
                    {
                        WritePageToDisk(page);
                    }

                    _pageMap.Remove(page.PageId);
                    page.IsValid = false;
                    page.PageId = -1;
                    page.UsageCount = 0;
                    page.IsDirty = false;
                }
            }
        }
        finally
        {
            _pageMapLock.ExitWriteLock();
        }
    }
}

/// <summary>
/// Buffer manager statistics
/// </summary>
public class BufferStats
{
    public int TotalPages { get; set; }
    public long PageHits { get; set; }
    public long PageMisses { get; set; }
    public long Evictions { get; set; }
    public double HitRatio { get; set; }
}

/// <summary>
/// Example usage and testing
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        // Create buffer manager with 10 buffer slots
        var bufferManager = new ClockSweepBufferManager(10);

        Console.WriteLine("=== Clock-Sweep Buffer Manager Demo ===");
        Console.WriteLine();

        // Access some pages
        Console.WriteLine("Accessing pages in sequence...");
        for (int i = 1; i <= 15; i++)
        {
            var page = bufferManager.AccessPage(i);
            Console.WriteLine($"Accessed page {i}");

            // Simulate some work with the page
            Thread.Sleep(10);

            // Unpin the page
            bufferManager.UnpinPage(i);
        }

        Console.WriteLine();
        Console.WriteLine("=== Buffer Statistics ===");
        var stats = bufferManager.GetStats();
        Console.WriteLine($"Total Pages: {stats.TotalPages}");
        Console.WriteLine($"Page Hits: {stats.PageHits}");
        Console.WriteLine($"Page Misses: {stats.PageMisses}");
        Console.WriteLine($"Evictions: {stats.Evictions}");
        Console.WriteLine($"Hit Ratio: {stats.HitRatio:P2}");

        Console.WriteLine();
        Console.WriteLine("=== Usage Count Distribution ===");
        var distribution = bufferManager.GetUsageCountDistribution();
        foreach (var kvp in distribution)
        {
            Console.WriteLine($"Usage Count {kvp.Key}: {kvp.Value} pages");
        }

        Console.WriteLine();
        Console.WriteLine("Re-accessing some pages to demonstrate hit ratio improvement...");

        // Re-access some pages to show hits
        for (int i = 10; i <= 15; i++)
        {
            var page = bufferManager.AccessPage(i);
            bufferManager.UnpinPage(i);
        }

        Console.WriteLine();
        Console.WriteLine("=== Updated Buffer Statistics ===");
        stats = bufferManager.GetStats();
        Console.WriteLine($"Total Pages: {stats.TotalPages}");
        Console.WriteLine($"Page Hits: {stats.PageHits}");
        Console.WriteLine($"Page Misses: {stats.PageMisses}");
        Console.WriteLine($"Evictions: {stats.Evictions}");
        Console.WriteLine($"Hit Ratio: {stats.HitRatio:P2}");
    }
}