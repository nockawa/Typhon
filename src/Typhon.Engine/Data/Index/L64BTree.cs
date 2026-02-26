// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;

namespace Typhon.Engine.BPTree;

[DebuggerTypeProxy(typeof(Index64Chunk.DebugView))]
[DebuggerDisplay("Count: {Count}, Start: {Start}, Flags: {StateFlags}")]
[StructLayout(LayoutKind.Sequential)]
unsafe public struct Index64Chunk
{
    // 128 bytes to span two cache lines (ALP prefetch: the Adjacent Line Prefetcher on Zen 4+/recent Intel automatically prefetches the paired 64-byte line
    //  within a naturally aligned 128-byte region)

    public const int Capacity = 9;

    // Special beast... Ownership control (LSB, 1 bit), state flags (LSW 15bits), Position of the first Item, aka Start (8bits), stored Item Count (8bits)
    public int Control;
    public int PrevChunk;
    public int NextChunk;
    public int LeftValue;
    public fixed int Values[Capacity];              // 9 × 4 = 36 bytes (+ 4 bytes implicit padding for long alignment)
    public fixed long Keys[Capacity];               // 9 × 8 = 72 bytes

    public Span<long> KeysAsSpan
    {
        get
        {
            fixed (long* k = Keys)
            {
                return new Span<long>(k, Capacity);
            }
        }
    }

    public Span<int> ValuesAsSpan
    {
        get
        {
            fixed (int* v = Values)
            {
                return new Span<int>(v, Capacity);
            }
        }
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            fixed (int* c = &Control)
            {
                return ((byte*)c)[3];
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            fixed (int* c = &Control)
            {
                ((byte*)c)[3] = (byte)value;
            }
        }
    }

    public int Start
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            fixed (int* c = &Control)
            {
                return ((byte*)c)[2];
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            fixed (int* c = &Control)
            {
                ((byte*)c)[2] = (byte)value;
            }
        }
    }

    public int End => Adjust(Start + Count);
    public NodeStates StateFlags
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            fixed (int* c = &Control)
            {
                return (NodeStates)((short*)c)[0];
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            fixed (int* c = &Control)
            {
                ((short*)c)[0] = (short)value;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool TryLock() => (Interlocked.Or(ref Control, (int)NodeStates.Ownership) & (int)NodeStates.Ownership) == 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void FreeLock() => Interlocked.And(ref Control, ~(int)NodeStates.Ownership);
    public bool IsLocked => (Control & (int)NodeStates.Ownership) != 0;
    public bool IsRotated => (Start + Count) > Capacity;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Adjust(int index) => (index < 0 || index >= Capacity) ? (index + Capacity * (-index).Sign()) : index;


    [ExcludeFromCodeCoverage]
    [DebuggerNonUserCode]
    private sealed class DebugView
    {
        private readonly Index64Chunk _chunk;

        public DebugView(Index64Chunk chunk)
        {
            _chunk = chunk;
        }

        public int Previous => _chunk.PrevChunk;
        public int Next => _chunk.NextChunk;
        public int LeftValue => _chunk.LeftValue;

        public ValueTuple<long, int>[] KeyValuePairs
        {
            get
            {
                var k = _chunk.KeysAsSpan;
                var v = _chunk.ValuesAsSpan;

                var s = _chunk.Start;
                var count = _chunk.Count;
                var res = new ValueTuple<long, int>[count];
                for (int i = 0; i < count; i++)
                {
                    var ii = Adjust(s + i);
                    res[i] = new ValueTuple<long, int>(k[ii], v[ii]);
                }

                return res;
            }
        }
    }
}

public abstract class L64BTree<TKey> : BTree<TKey> where TKey : unmanaged
{
    unsafe public class L64NodeStorage : BaseNodeStorage
    {
        internal override void Initialize(BTree<TKey> owner, ChunkBasedSegment segment)
        {
            base.Initialize(owner, segment);
            Debug.Assert(sizeof(Index64Chunk) == 128);
            Debug.Assert(segment.Stride == sizeof(Index64Chunk));
        }

        #region Chunk Properties Access

        public override void InitializeNode(NodeWrapper node, NodeStates states, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            chunk.Control = (int)states;  // Atomically sets StateFlags + Start=0 + Count=0
            chunk.PrevChunk = 0;
            chunk.NextChunk = 0;
            chunk.LeftValue = 0;
        }

        public override int GetNodeCapacity() => Index64Chunk.Capacity;

        public override NodeWrapper GetLeftNode(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.LeftValue);
        }

        public override void SetLeftNode(NodeWrapper node, int previousNodeId, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            chunk.LeftValue = previousNodeId;
        }

        public override NodeWrapper GetPreviousNode(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.PrevChunk);
        }

        public override void SetPreviousNode(NodeWrapper node, int previousNodeId, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            chunk.PrevChunk = previousNodeId;
        }

        public override NodeWrapper GetNextNode(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.NextChunk);
        }

        public override void SetNextNode(NodeWrapper node, int nextNodeId, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            chunk.NextChunk = nextNodeId;
        }

        public override KeyValueItem GetItem(NodeWrapper node, int index, bool adjust, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            var i = adjust ? Index64Chunk.Adjust(chunk.Start + index) : index;
            long chunkKey = chunk.Keys[i];
            return new KeyValueItem(*(TKey*)&chunkKey, chunk.Values[i]);
        }

        public override void SetItem(NodeWrapper node, int index, KeyValueItem value, bool adjust, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            Set(ref chunk, index, value, adjust);
        }

        public override int GetCount(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            return chunk.Count;
        }

        public override void SetCount(NodeWrapper node, int value, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            chunk.Count = value;
        }

        public override int GetStart(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            return chunk.Start;
        }

        public override void SetStart(NodeWrapper node, int value, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            chunk.Start = value;
        }

        public override int GetEnd(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            return Index64Chunk.Adjust(chunk.Start + chunk.Count);
        }

        public override NodeStates GetNodeStates(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            return chunk.StateFlags;
        }

        #endregion

        #region Chunk Operations

        public override void PushFirst(NodeWrapper node, KeyValueItem item, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);

            DecrementStart(ref chunk);

            var start = chunk.Start;
            chunk.Keys[start] = *(long*)&item.Key;
            chunk.Values[start] = item.Value;

            ++chunk.Count;
        }

        public override void PushLast(NodeWrapper node, KeyValueItem item, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            var c = chunk.Count++;
            Set(ref chunk, c, item, true);
        }

        public override int Append(int bufferId, int value, ref ChunkAccessor accessor) => throw new Exception("Shouldn't be called as key replace is not supported and multi-value neither");

        public override void Insert(NodeWrapper node, int index, KeyValueItem item, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            var lsh = index; // length of left shift
            var rsh = chunk.Count - index; // length of right shift

            if (lsh < rsh) // choose least shifts required
            {
                LeftShift(ref chunk, chunk.Start, lsh); // move Start to Start-1
                Set(ref chunk, index - 1, item, true);
                DecrementStart(ref chunk);
            }
            else
            {
                RightShift(ref chunk, Index64Chunk.Adjust(chunk.Start + index), rsh); // move End to End+1
                Set(ref chunk, index, item, true);
            }

            chunk.Count++;
        }

        public override int CreateBuffer(ref ChunkAccessor accessor) => default;

        public override VariableSizedBufferAccessor<int> GetBufferReadOnlyAccessor(int bufferId, ref ChunkAccessor accessor) => default;
        public override int RemoveFromBuffer(int bufferId, int elementId, int value, ref ChunkAccessor accessor) => default;
        public override void DeleteBuffer(int bufferId, ref ChunkAccessor accessor) { }

        public override NodeWrapper GetFirstChild(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.LeftValue);
        }

        public override bool IsRotated(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            return (chunk.Start + chunk.Count) > Index64Chunk.Capacity;
        }

        public override int BinarySearch(NodeWrapper node, TKey key, IComparer<TKey> comparer, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            fixed (long* keys = chunk.Keys)
            {
                // SIMD fast path for long keys (signed comparison — ulong is stored as long with signed ordering)
                if (typeof(TKey) == typeof(long))
                {
                    return SimdSearch(keys, chunk.Start, chunk.Count, *(long*)&key);
                }

                // Fallback to comparer-based binary search for other types (double)
                bool rotated = (chunk.Start + chunk.Count) > Index64Chunk.Capacity;
                if (rotated)
                {
                    long chunkKey = chunk.Keys[Index64Chunk.Capacity - 1];
                    if (comparer.Compare(key, *(TKey*)&chunkKey) <= 0)
                    {
                        var find = BTreeExtensions.BinarySearch((TKey*)keys, chunk.Start, Index64Chunk.Capacity - chunk.Start, key, comparer, sizeof(long));
                        return find - chunk.Start * find.Sign();
                    }
                    else
                    {
                        var find = BTreeExtensions.BinarySearch((TKey*)keys, 0, chunk.End, key, comparer, sizeof(long));
                        return find + (Index64Chunk.Capacity - chunk.Start) * find.Sign();
                    }
                }
                else
                {
                    var find = BTreeExtensions.BinarySearch((TKey*)keys, chunk.Start, chunk.Count, key, comparer, sizeof(long));
                    return find - chunk.Start * find.Sign();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static int SimdSearch(long* keys, int start, int count, long searchKey)
        {
            int pos;
            if ((start + count) <= Index64Chunk.Capacity)
            {
                pos = CountLessThan(keys + start, count, searchKey);
                if (pos < count && keys[start + pos] == searchKey)
                {
                    return pos;
                }
            }
            else
            {
                int rightCount = Index64Chunk.Capacity - start;
                pos = CountLessThan(keys + start, rightCount, searchKey)
                    + CountLessThan(keys, count - rightCount, searchKey);
                if (pos < count)
                {
                    int physIdx = start + pos;
                    if (physIdx >= Index64Chunk.Capacity)
                    {
                        physIdx -= Index64Chunk.Capacity;
                    }
                    if (keys[physIdx] == searchKey)
                    {
                        return pos;
                    }
                }
            }
            return ~pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static int CountLessThan(long* keys, int count, long searchKey)
        {
            int pos = 0;
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                var needle = Vector256.Create(searchKey);
                for (; i + 4 <= count; i += 4)
                {
                    var cmp = Vector256.LessThan(Vector256.Load(keys + i), needle);
                    pos += BitOperations.PopCount(cmp.ExtractMostSignificantBits());
                }
            }
            if (Vector128.IsHardwareAccelerated)
            {
                var needle128 = Vector128.Create(searchKey);
                for (; i + 2 <= count; i += 2)
                {
                    var cmp = Vector128.LessThan(Vector128.Load(keys + i), needle128);
                    pos += BitOperations.PopCount(cmp.ExtractMostSignificantBits());
                }
            }
            for (; i < count; i++)
            {
                pos += keys[i] < searchKey ? 1 : 0;
            }
            return pos;
        }

        public override NodeWrapper SplitRight(NodeWrapper node, NodeStates states, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            return SplitRight(ref chunk, states, ref accessor);
        }

        public override KeyValueItem RemoveAt(NodeWrapper node, int index, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            var item = GetItem(node, index, true, ref accessor);

            var lsh = chunk.Count - index - 1; // length of left shift
            var rsh = index; // length of right shift

            if (rsh < lsh) // choose least shifts required
            {
                RightShift(ref chunk, chunk.Start, rsh); // move Start to Start+1
                Set(ref chunk, chunk.Start, default, false);
                IncrementStart(ref chunk);
            }
            else
            {
                LeftShift(ref chunk, Index64Chunk.Adjust(chunk.Start + index + 1), lsh); // move End to End-1
                Set(ref chunk, chunk.Count - 1, default, true); // remove last item
            }

            chunk.Count--;
            return item;
        }

        public override void MergeLeft(NodeWrapper left, NodeWrapper right, ref ChunkAccessor accessor)
        {
            ref var leftChunk = ref accessor.GetChunk<Index64Chunk>(left.ChunkId, true);
            ref var rightChunk = ref accessor.GetChunk<Index64Chunk>(right.ChunkId, true);

            var lk = leftChunk.KeysAsSpan;
            var lv = leftChunk.ValuesAsSpan;
            var rk = rightChunk.KeysAsSpan;
            var rv = rightChunk.ValuesAsSpan;

            if (leftChunk.Count + right.GetCount(ref accessor) > Index64Chunk.Capacity)
            {
                throw new InvalidOperationException("can not merge, there is not enough capacity for this array.");
            }

            var end = leftChunk.Start + leftChunk.Count;

            if (leftChunk.IsRotated)
            {
                var start = end - Index64Chunk.Capacity;

                if (!rightChunk.IsRotated)
                {
                    rk.Slice(right.GetStart(ref accessor), right.GetCount(ref accessor)).CopyTo(lk.Slice(start, right.GetCount(ref accessor)));
                    rv.Slice(right.GetStart(ref accessor), right.GetCount(ref accessor)).CopyTo(lv.Slice(start, right.GetCount(ref accessor)));
                }
                else
                {
                    var srLen = right.GetCapacity() - right.GetStart(ref accessor); // right length
                    var slLen = right.GetCount(ref accessor) - srLen; // left length (remaining)

                    rk.Slice(right.GetStart(ref accessor), srLen).CopyTo(lk.Slice(start, srLen));
                    rv.Slice(right.GetStart(ref accessor), srLen).CopyTo(lv.Slice(start, srLen));

                    rk.Slice(0, slLen).CopyTo(lk.Slice(start + srLen, slLen));
                    rv.Slice(0, slLen).CopyTo(lv.Slice(start + srLen, slLen));
                }
            }
            else
            {
                bool copyIsOnePiece = end + right.GetCount(ref accessor) <= Index64Chunk.Capacity;

                if (!rightChunk.IsRotated)
                {
                    if (copyIsOnePiece)
                    {
                        rk.Slice(right.GetStart(ref accessor), right.GetCount(ref accessor)).CopyTo(lk.Slice(end, right.GetCount(ref accessor)));
                        rv.Slice(right.GetStart(ref accessor), right.GetCount(ref accessor)).CopyTo(lv.Slice(end, right.GetCount(ref accessor)));
                    }
                    else
                    {
                        var length = Index64Chunk.Capacity - end;
                        var remaining = right.GetCount(ref accessor) - length;

                        rk.Slice(right.GetStart(ref accessor), length).CopyTo(lk.Slice(end, length));
                        rk.Slice(right.GetStart(ref accessor) + length, remaining).CopyTo(lk.Slice(0, remaining));

                        rv.Slice(right.GetStart(ref accessor), length).CopyTo(lv.Slice(end, length));
                        rv.Slice(right.GetStart(ref accessor) + length, remaining).CopyTo(lv.Slice(0, remaining));
                    }
                }
                else
                {
                    var srLen = right.GetCapacity() - right.GetStart(ref accessor); // right length
                    var slLen = right.GetCount(ref accessor) - srLen; // left length (remaining)

                    if (copyIsOnePiece)
                    {
                        rk.Slice(right.GetStart(ref accessor), srLen).CopyTo(lk.Slice(end, srLen));
                        rk.Slice(0, slLen).CopyTo(lk.Slice(end + srLen, slLen));

                        rv.Slice(right.GetStart(ref accessor), srLen).CopyTo(lv.Slice(end, srLen));
                        rv.Slice(0, slLen).CopyTo(lv.Slice(end + srLen, slLen));
                    }
                    else
                    {
                        var mergeEnd = end + srLen;

                        if (mergeEnd <= Index64Chunk.Capacity)
                        {
                            var secondCopyFirstLength = Index64Chunk.Capacity - mergeEnd;
                            var secondCopySecondLength = slLen - secondCopyFirstLength;

                            rk.Slice(right.GetStart(ref accessor), srLen).CopyTo(lk.Slice(end, srLen));
                            rv.Slice(right.GetStart(ref accessor), srLen).CopyTo(lv.Slice(end, srLen));

                            rk.Slice(0, secondCopyFirstLength).CopyTo(lk.Slice(mergeEnd, secondCopyFirstLength));
                            rv.Slice(0, secondCopyFirstLength).CopyTo(lv.Slice(mergeEnd, secondCopyFirstLength));
                            rk.Slice(secondCopyFirstLength, secondCopySecondLength).CopyTo(lk.Slice(0, secondCopySecondLength));
                            rv.Slice(secondCopyFirstLength, secondCopySecondLength).CopyTo(lv.Slice(0, secondCopySecondLength));
                        }
                        else
                        {
                            var firstCopyFirstLength = Index64Chunk.Capacity - end;
                            var firstCopySecondLength = srLen - firstCopyFirstLength;
                            var firstCopySecondStart = right.GetStart(ref accessor) + firstCopyFirstLength;

                            rk.Slice(right.GetStart(ref accessor), firstCopyFirstLength).CopyTo(lk.Slice(end, firstCopyFirstLength));
                            rk.Slice(firstCopySecondStart, firstCopySecondLength).CopyTo(lk.Slice(0, firstCopySecondLength));
                            rv.Slice(right.GetStart(ref accessor), firstCopyFirstLength).CopyTo(lv.Slice(end, firstCopyFirstLength));
                            rv.Slice(firstCopySecondStart, firstCopySecondLength).CopyTo(lv.Slice(0, firstCopySecondLength));

                            rk.Slice(0, slLen).CopyTo(lk.Slice(firstCopySecondLength, slLen));
                            rv.Slice(0, slLen).CopyTo(lv.Slice(firstCopySecondLength, slLen));
                        }
                    }
                }
            }

            leftChunk.Count += right.GetCount(ref accessor); // correct array length.
        }

        public override NodeWrapper GetLastChild(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index64Chunk>(node.ChunkId);
            var index = Index64Chunk.Adjust(chunk.Start + chunk.Count - 1);
            return new NodeWrapper(this, chunk.Values[index]);
        }

        public override void IncrementStart(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            IncrementStart(ref chunk);
        }

        public override void DecrementStart(NodeWrapper node, ref ChunkAccessor accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index64Chunk>(node.ChunkId, true);
            DecrementStart(ref chunk);
        }

        #endregion

        #region Chunk Direct Access Wrappers

        private static void Set(ref Index64Chunk chunk, int index, KeyValueItem item, bool adjust)
        {
            var i = adjust ? Index64Chunk.Adjust(chunk.Start + index) : index;
            chunk.Keys[i] = *(long*)&item.Key;
            chunk.Values[i] = item.Value;
        }

        private static void DecrementStart(ref Index64Chunk chunk)
        {
            if (chunk.Start == 0)
            {
                chunk.Start = Index64Chunk.Capacity - 1;
            }
            else
            {
                --chunk.Start;
            }
        }

        private static void IncrementStart(ref Index64Chunk chunk)
        {
            if (chunk.Start == (Index64Chunk.Capacity - 1))
            {
                chunk.Start = 0;
            }
            else
            {
                ++chunk.Start;
            }
        }

        private void LeftShift(ref Index64Chunk chunk, int index, int length)
        {
            if (length == 0)
            {
                return;
            }

            if (length < 0 || length > Index64Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (index < 0 || index >= Index64Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var k = chunk.KeysAsSpan;
            var v = chunk.ValuesAsSpan;

            if (index == 0)
            {
                var firstK = k[0];
                k.Slice(1, length - 1).CopyTo(k);
                k[^1] = firstK;

                var firstV = v[0];
                v.Slice(1, length - 1).CopyTo(v);
                v[^1] = firstV;
            }
            else if (index + length > k.Length)
            {
                var l = index + length - k.Length - 1;
                var remaining = length - l - 1;
                var firstK = k[0];
                k.Slice(1, l).CopyTo(k.Slice(0, l));
                k.Slice(index, remaining).CopyTo(k.Slice(index - 1, remaining));
                k[^1] = firstK;

                var firstV = v[0];
                v.Slice(1, l).CopyTo(v.Slice(0, l));
                v.Slice(index, remaining).CopyTo(v.Slice(index - 1, remaining));
                v[^1] = firstV;
            }
            else
            {
                k.Slice(index, length).CopyTo(k.Slice(index - 1, length));
                v.Slice(index, length).CopyTo(v.Slice(index - 1, length));
            }
        }

        private void RightShift(ref Index64Chunk chunk, int index, int length)
        {
            if (length == 0)
            {
                return;
            }

            if (length < 0 || length > Index64Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (index < 0 || index >= Index64Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var k = chunk.KeysAsSpan;
            var v = chunk.ValuesAsSpan;

            var lastInd = Index64Chunk.Capacity - 1;
            if (index + length > lastInd) // if overflows, rotate.
            {
                var lastK = k[lastInd];
                var rl = lastInd - index;
                var remaining = length - rl - 1;
                k.Slice(index, rl).CopyTo(k.Slice(index + 1, rl));
                k.Slice(0, remaining).CopyTo(k.Slice(1, remaining));
                k[0] = lastK;

                var lastV = v[lastInd];
                v.Slice(index, rl).CopyTo(v.Slice(index + 1, rl));
                v.Slice(0, remaining).CopyTo(v.Slice(1, remaining));
                v[0] = lastV;
            }
            else
            {
                k.Slice(index, length).CopyTo(k.Slice(index + 1, length));
                v.Slice(index, length).CopyTo(v.Slice(index + 1, length));
            }
        }

        public NodeWrapper SplitRight(ref Index64Chunk left, NodeStates states, ref ChunkAccessor accessor)
        {
            var rightNode = Owner.AllocNode(states, ref accessor);
            ref var right = ref accessor.GetChunk<Index64Chunk>(rightNode.ChunkId, true);

            var lr = left.Count / 2; // length of right side
            var lrc = 1 + ((left.Count - 1) / 2); // length of right (ceiling of Length/2)
            var sr = Index64Chunk.Adjust(left.Start + lrc); // start of right side

            right.Count = lr;
            left.Count -= right.Count;

            var lk = left.KeysAsSpan;
            var lv = left.ValuesAsSpan;
            var rk = right.KeysAsSpan;
            var rv = right.ValuesAsSpan;

            var capacity = Index64Chunk.Capacity;

            if (sr + lr <= capacity) // if right side is one piece
            {
                lk.Slice(sr, lr).CopyTo(rk.Slice(0, lr));
                lk.Slice(sr, lr).Clear();
                lv.Slice(sr, lr).CopyTo(rv.Slice(0, lr));
                lv.Slice(sr, lr).Clear();
            }
            else
            {
                var length = capacity - sr;

                lk.Slice(sr, length).CopyTo(rk.Slice(0, length));
                lk.Slice(sr, length).Clear();
                lv.Slice(sr, length).CopyTo(rv.Slice(0, length));
                lv.Slice(sr, length).Clear();

                var remaining = lr - length;
                lk.Slice(0, remaining).CopyTo(rk.Slice(length, remaining));
                lk.Slice(0, remaining).Clear();
                lv.Slice(0, remaining).CopyTo(rv.Slice(length, remaining));
                lv.Slice(0, remaining).Clear();
            }

            return rightNode;
        }

        #endregion
    }

    protected override BaseNodeStorage GetStorage() => new L64NodeStorage();
    public override bool AllowMultiple => false;
    protected L64BTree(ChunkBasedSegment segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }
}

public class L64MultipleBTree<TKey> : L64BTree<TKey> where TKey : unmanaged
{
    public L64MultipleBTree(ChunkBasedSegment segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }

    public override bool AllowMultiple => true;
    protected override BaseNodeStorage GetStorage() => new L64MultipleNodeStorage();

    public class L64MultipleNodeStorage : L64NodeStorage
    {
        private VariableSizedBufferSegment<int> _valueStore;

        internal override void Initialize(BTree<TKey> owner, ChunkBasedSegment segment)
        {
            base.Initialize(owner, segment);
            _valueStore = new VariableSizedBufferSegment<int, IndexBufferExtraHeader>(segment);

        }

        public override int Append(int bufferId, int value, ref ChunkAccessor accessor) => _valueStore.AddElement(bufferId, value, ref accessor);
        public override VariableSizedBufferAccessor<int> GetBufferReadOnlyAccessor(int bufferId, ref ChunkAccessor accessor) => _valueStore.GetReadOnlyAccessor(bufferId);

        public override int CreateBuffer(ref ChunkAccessor accessor) => _valueStore.AllocateBuffer(ref accessor);

        public override int RemoveFromBuffer(int bufferId, int elementId, int value, ref ChunkAccessor accessor) 
            => _valueStore.DeleteElement(bufferId, elementId, value, ref accessor);
        public override void DeleteBuffer(int bufferId, ref ChunkAccessor accessor) => _valueStore.DeleteBuffer(bufferId, ref accessor);
    }
}

public class LongSingleBTree : L64BTree<long>
{
    public LongSingleBTree(ChunkBasedSegment segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }
}

public class LongMultipleBTree : L64MultipleBTree<long>
{
    public LongMultipleBTree(ChunkBasedSegment segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }
}
public class ULongSingleBTree : L64BTree<long>
{
    public ULongSingleBTree(ChunkBasedSegment segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }
}

public class ULongMultipleBTree : L64MultipleBTree<long>
{
    public ULongMultipleBTree(ChunkBasedSegment segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }
}

public class DoubleSingleBTree : L64BTree<double>
{
    public DoubleSingleBTree(ChunkBasedSegment segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }
}

public class DoubleMultipleBTree : L64MultipleBTree<double>
{
    public DoubleMultipleBTree(ChunkBasedSegment segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }
}