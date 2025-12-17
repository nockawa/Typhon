using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

[PublicAPI]
internal ref struct ComponentRevisionManager
{
    internal const int CompRevChunkSize = 64;
    internal unsafe static readonly int CompRevCountInRoot = (CompRevChunkSize - sizeof(CompRevStorageHeader)) / sizeof(CompRevStorageElement);
    internal unsafe static readonly int CompRevCountInNext = (CompRevChunkSize / sizeof(CompRevStorageElement));

    internal ref struct ElementRevisionHandle : IDisposable
    {
        private ChunkHandle _handle;
        private readonly bool _isFirst;
        private readonly short _elementIndex;

        public ElementRevisionHandle(ChunkHandle handle, bool isFirst, short elementIndex)
        {
            _handle = handle;
            _isFirst = isFirst;
            _elementIndex = elementIndex;
        }

        public unsafe ref CompRevStorageElement Element
        {
            get
            {
                var headerSize = _isFirst ? sizeof(CompRevStorageHeader) : sizeof(int);
                return ref _handle.AsSpan().Slice(headerSize).Cast<byte, CompRevStorageElement>().Slice(_elementIndex, 1)[0];
            }
        }
        
        public void Commit(long tsn)
        {
            ref var el = ref Element;
            el.TSN = tsn;
            el.IsolationFlag = false;
        }
        
        public void Dispose() => _handle.Dispose();
    }
    
    internal static ElementRevisionHandle GetRevisionElement(ChunkRandomAccessor accessor, int firstChunkId, short revisionIndex)
    {
        var firstHandle = accessor.GetChunkHandle(firstChunkId, false);
        ref var firstHeader = ref firstHandle.AsRef<CompRevStorageHeader>();
        if (revisionIndex < CompRevCountInRoot)
        {
            return new ElementRevisionHandle(firstHandle, true, revisionIndex);
        }

        var (chunkIndexInChain, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(revisionIndex);

        // Walk through the linked list until we find the chunk that is our starting point
        var nextChunkId = firstHeader.NextChunkId;

        var curHandle = accessor.GetChunkHandle(nextChunkId, false);
        var useLock = !firstHeader.Control.IsLockedByCurrentThread;
        if (useLock)
        {
            firstHeader.Control.EnterSharedAccess();
        }
        while (--chunkIndexInChain >= 0)
        {
            curHandle.Dispose();
            curHandle = accessor.GetChunkHandle(nextChunkId, false);
            nextChunkId = curHandle.AsRef<int>();
        }

        if (useLock)
        {
            firstHeader.Control.ExitSharedAccess();
        }

        firstHandle.Dispose();
        return new ElementRevisionHandle(curHandle, false, (short)indexInChunk);
    }

    internal static void AddCompRev(Transaction.ComponentInfoBase info, ref Transaction.ComponentInfoBase.CompRevInfo compRevInfo, long tsn, bool isDelete)
    {
        var compRevTableAccessor = info.CompRevTableAccessor;
        var compContent = info.CompContentSegment;

        using var handle = compRevTableAccessor.GetChunkHandle(compRevInfo.CompRevTableFirstChunkId, true);
        var stream = handle.AsStream();

        // Get the chunk of the header
        ref var firstHeader = ref stream.PopRef<CompRevStorageHeader>();

        // Enter exclusive access for the Revision Table
        firstHeader.Control.EnterExclusiveAccess();

        // Check if we need to add one more chunk to the chain
        if (ComputeRevElementCount(firstHeader.ChainLength) == firstHeader.ItemCount)
        {
            GrowChain(info, compRevInfo.CompRevTableFirstChunkId, ref firstHeader);
        }

        // Add our new entry
        var newRevIndex = (short)(firstHeader.FirstItemIndex + firstHeader.ItemCount);
        var indexInChunk = GetRevisionLocation(compRevTableAccessor, compRevInfo.CompRevTableFirstChunkId, newRevIndex, out var curChunkId);

        Span<CompRevStorageElement> curChunkElements;
        ChunkHandle curChunkHandle = default;

        // Still in the first chunk? The elements are right after
        if (compRevInfo.CompRevTableFirstChunkId == curChunkId)
        {
            curChunkElements = stream.PopSpan<CompRevStorageElement>(CompRevCountInRoot);
        }
        
        // In another chunk, the subsequent ones have a one int header (the ID of the next chunk in the chain), then the elements
        else
        {
            curChunkHandle = compRevTableAccessor.GetChunkHandle(curChunkId, true);
            curChunkElements = curChunkHandle.AsSpan().Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
        } 

        // Allocate a new component
        var componentChunkId = isDelete ? 0 : compContent.AllocateChunk(false);

        // Add our new entry
        curChunkElements[indexInChunk].TSN = tsn;
        curChunkElements[indexInChunk].IsolationFlag = true;
        curChunkElements[indexInChunk].ComponentChunkId = componentChunkId;

        // Update the compRevInfo
        compRevInfo.PrevCompContentChunkId = compRevInfo.CurCompContentChunkId;
        compRevInfo.PrevRevisionIndex = compRevInfo.CurRevisionIndex;
        compRevInfo.CurCompContentChunkId = componentChunkId;
        compRevInfo.CurRevisionIndex = newRevIndex;

        // One more item, update the header
        firstHeader.ItemCount++;
        
        // Cleanups
        curChunkHandle.Dispose();
        firstHeader.Control.ExitExclusiveAccess();
    }

    internal static int AllocCompRevStorage(Transaction.ComponentInfoBase info, long tsn, int firstChunkId)
    {
        var chunkId = info.CompRevTableSegment.AllocateChunk(false);
        using var handle = info.CompRevTableAccessor.GetChunkHandle(chunkId, true);
        var stream = handle.AsStream();
        
        ref var header = ref stream.PopRef<CompRevStorageHeader>();
        
        // Initialize the header
        header.NextChunkId = 0;
        header.FirstItemRevision = 1;
        header.Control = default;
        header.FirstItemIndex = 0;
        header.ItemCount = 1;
        header.ChainLength = 1;
        header.LastCommitRevisionIndex = -1;

        // Initialize the first element
        ref var chunkElements = ref stream.PopRef<CompRevStorageElement>();
        chunkElements.TSN = tsn;
        chunkElements.IsolationFlag = true;                                  // Isolate this revision from the rest of the database (other transactions)
        chunkElements.ComponentChunkId = firstChunkId;

        return chunkId;
    }

    /// <summary>
    /// Clean up the revisions of a component, removing all the entries older than <paramref name="nextMinTSN"/>, releasing unused component chunks and
    ///  defragmenting the revisions still being used.
    /// </summary>
    /// <param name="info">ComponentInfo object</param>
    /// <param name="compRevInfo">Component Revision Info object</param>
    /// <param name="compRevTableAccessor">The accessor</param>
    /// <param name="nextMinTSN">The minimal TSN to keep revisions</param>
    /// <remarks>
    /// This method walks through the chain of revision chunks and builds a new one, only the first chunk is kept.
    /// </remarks>
    internal static bool CleanUpUnusedEntries(Transaction.ComponentInfoBase info, ref Transaction.ComponentInfoBase.CompRevInfo compRevInfo, 
        ChunkRandomAccessor compRevTableAccessor, long nextMinTSN)
    {
        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;
        using var firstChunkHandle = compRevTableAccessor.GetChunkHandle(firstChunkId, false);
        ref var firstChunkHeader = ref firstChunkHandle.AsRef<CompRevStorageHeader>();
        
        // Create a temporary chunk to store the cleaned-up content of the first chunk (we can't overwrite the first chunk right away)
        Span<byte> tempChunk = stackalloc byte[CompRevChunkSize];
        tempChunk.Clear();
        tempChunk.Split(out Span<CompRevStorageHeader> tempFirstHeader, out Span<CompRevStorageElement> tempElements);
        tempFirstHeader[0].ChainLength = 1;
        var curNextChunkId = tempChunk.Slice(0, sizeof(int)).Cast<byte, int>();
        var curDestElements = tempElements;
        var curDestIndex = 0;
        var curDestIndexInChunk = 0;
        var skipCount = 0;
        var ct = info.ComponentTable;
        var hasCollections = ct.HasCollections;

        ChunkHandle newChunkHandle = default;
        {
            using var enumerator = new RevisionEnumerator(compRevTableAccessor, firstChunkId, false, true);
            var prevChunkId = enumerator.IndexInChunk == 0 ? enumerator.CurChunkId : 0;
            var maxSkipCount = firstChunkHeader.ItemCount;
            var skipping = true;
            while (enumerator.MoveNext())
            {
                bool changedChunk = (enumerator.CurChunkId != prevChunkId) && (prevChunkId != 0);
                if (changedChunk)
                {
                    // Remove the previous chunk if we can
                    if (prevChunkId != 0 && !enumerator.IsFirstChunk)
                    {
                        if (hasCollections)
                        {
                            foreach (var kvp in ct.ComponentCollectionVSBSByOffset)
                            {
                                var bufferId = info.CompContentAccessor.GetChunkAsReadOnlySpan(prevChunkId).Slice(kvp.Key).Cast<byte, int>()[0];
                                kvp.Value.Item1.BufferRelease(bufferId, kvp.Value.Item2);
                            }
                        }

                        info.CompContentSegment.FreeChunk(prevChunkId);
                    }
                    prevChunkId = enumerator.CurChunkId;
                }

                if (skipping)
                {
                    // If the entry is older than the minimum tick, or we reached the maximum number of entries we can skip,
                    //  we can remove it and skip to the next one
                    if ((--maxSkipCount > 0) && (enumerator.Current.TSN < nextMinTSN))
                    {
                        // Check if there's a component chunk to free
                        var revChunkId = enumerator.Current.ComponentChunkId;
                        if (revChunkId != 0)
                        {
                            if (hasCollections)
                            {
                                foreach (var kvp in ct.ComponentCollectionVSBSByOffset)
                                {
                                    var bufferId = info.CompContentAccessor.GetChunkAsReadOnlySpan(revChunkId).Slice(kvp.Key).Cast<byte, int>()[0];
                                    kvp.Value.Item1.BufferRelease(bufferId, kvp.Value.Item2);
                                }
                            }

                            info.CompContentSegment.FreeChunk(revChunkId);
                        }
            
                        // Clear the entry
                        enumerator.CurrentAsSpan.Clear();
                    
                        skipCount++;
                        continue;
                    }
                
                    // We stop skipping at the first valid entry
                    skipping = false;
                }
            
                curDestElements[curDestIndexInChunk++] = enumerator.Current;            // Copy the revision to the destination
                tempFirstHeader[0].ItemCount++;                                         // Update the item count
                if (!enumerator.Current.IsolationFlag)                                  // Update the last committed revision index if this is not an isolated entry
                {
                    tempFirstHeader[0].LastCommitRevisionIndex = (short)curDestIndex;
                }
                curDestIndex++;                                                         // One more item in the destination
            
                // If the current chunk is full, allocate a new one
                if (curDestIndex == curDestElements.Length)
                {
                    curDestIndexInChunk = 0;                                            // Reset the index in chunk
                    tempFirstHeader[0].ChainLength++;                                   // One more chunk in the chain
                    var newChunkId = info.CompRevTableSegment.AllocateChunk(false); // Allocate a new chunk
                    curNextChunkId[0] = newChunkId;                                     // Set the next chunk ID of the current chunk
                    if (!newChunkHandle.IsDefault)                                      // Release the handle on the previous chunk, if any
                    {
                        newChunkHandle.Dispose();
                    }
                    newChunkHandle = compRevTableAccessor.GetChunkHandle(newChunkId, true);     // Get the handle of the new chunk
                    newChunkHandle.AsSpan().Split(out curNextChunkId, out curDestElements);     // Update our "cur" variables
                }
            }
        }
        
        tempFirstHeader[0].FirstItemRevision = firstChunkHeader.FirstItemRevision + skipCount;
        if (!newChunkHandle.IsDefault)
        {
            newChunkHandle.Dispose();
        }
        var tempControl = firstChunkHeader.Control;
        tempChunk.CopyTo(firstChunkHandle.AsSpan());
        firstChunkHeader.Control = tempControl;

        compRevInfo.CurRevisionIndex = 0;   // As we defrag and move everything to the beginning of the chunk, the first revision is always at 0

        // Is the component totally deleted? Return true, otherwise false
        return (tempFirstHeader[0].ItemCount == 1 && tempElements[0].ComponentChunkId == 0);
    }

    private static void GrowChain(Transaction.ComponentInfoBase info, int firstChunkId, ref CompRevStorageHeader firstHeader)
    {
        var compRevTableAccessor = info.CompRevTableAccessor;
        var compRevTable = info.CompRevTableSegment;
        
        // Special case, the first revision is in the first chunk, we need to walk to the end of the chain and add a new chunk there
        if (firstHeader.FirstItemIndex < CompRevCountInRoot)
        {
            var enumerator = new RevisionEnumerator(compRevTableAccessor, firstChunkId, false, false);
            enumerator.StepToChunk(firstHeader.ChainLength - 1, false);         // Walk to the last chunk in the chain
            enumerator.NextChunkId = compRevTable.AllocateChunk(true);          // Allocated, clear content to make sure the next chunk ID is 0, set as next
            compRevTableAccessor.DirtyChunk(enumerator.CurChunkId);
            firstHeader.ChainLength++;
        }
        else
        {
            // Locate the first index in the chain, we add a chunk just before it
            var (firstChunkInChain, firstItemIndexInChunk) = CompRevStorageHeader.GetRevisionLocation(firstHeader.FirstItemIndex);
            var enumerator = new RevisionEnumerator(compRevTableAccessor, firstChunkId, false, false);
            enumerator.StepToChunk(firstChunkInChain-1, false);                 // In a circular buffer, the chunk before the first is the last one

            // Get the ID of the first chunk in the chain
            var firstChunkIndexInChain = enumerator.NextChunkId;
            
            // Add a new chunk after the last in the chain
            var newChunkId = compRevTable.AllocateChunk(true);              // Clear content to make sure the next chunk ID is 0
            enumerator.NextChunkId = newChunkId;
            compRevTableAccessor.DirtyChunk(enumerator.CurChunkId);

            // Copy the elements from the first chunk to the new chunk
            using var newChunkHandle = compRevTableAccessor.GetChunkHandle(newChunkId, true);
            using var firstChunkHandle = compRevTableAccessor.GetChunkHandle(firstChunkIndexInChain, true);
            var newChunkElements = newChunkHandle.AsSpan().Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
            var firstChunkElements = firstChunkHandle.AsSpan().Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
            firstChunkElements.Slice(0, firstItemIndexInChunk).CopyTo(newChunkElements);
            
            firstHeader.ChainLength++;                                              // One more item in the chain
            firstHeader.FirstItemIndex += (short)CompRevCountInNext; // We added a chunk before, the first item index gets shifted
        }
        compRevTableAccessor.DirtyChunk(firstChunkId);
    }

    private static int ComputeRevElementCount(int chainLength) => CompRevCountInRoot + ((chainLength - 1) * CompRevCountInNext);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe short GetRevisionLocation(ChunkRandomAccessor accessor, int firstChunkId, short revisionIndex, out int resChunkId)
    {
        if (revisionIndex < CompRevCountInRoot)
        {
            resChunkId = firstChunkId;
            return revisionIndex;
        }

        var (chunkIndexInChain, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(revisionIndex);

        // Walk through the linked list until we find the chunk that is our starting point
        var header = (CompRevStorageHeader*)accessor.GetChunkAddress(firstChunkId);
        resChunkId = header->NextChunkId;

        var first = header;
        var useLock = !first->Control.IsLockedByCurrentThread;
        if (useLock)
        {
            first->Control.EnterSharedAccess();
        }
        while (--chunkIndexInChain != 0)
        {
            resChunkId = *(int*)accessor.GetChunkAddress(resChunkId);
        }
        if (useLock)
        {
            first->Control.ExitSharedAccess();
        }

        return (short)indexInChunk;
    }
}