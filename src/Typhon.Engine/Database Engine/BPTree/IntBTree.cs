// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Typhon.Engine.BPTree
{
    public class IntSingleBTree : IntBTree
    {
        public IntSingleBTree(ChunkBasedSegment segment, ChunkBasedSegmentAccessorPool pool) : base(segment, pool)
        {
        }

        protected override bool AllowMultiple => false;
    }

    public class IntMultipleBTree : IntBTree
    {
        public IntMultipleBTree(ChunkBasedSegment segment, ChunkBasedSegmentAccessorPool pool) : base(segment, pool)
        {
        }

        protected override bool AllowMultiple => true;
        protected override BaseNodeStorage GetStorage() => new IntMultipleNodeStorage();

        public class IntMultipleNodeStorage : IntNodeStorage
        {
            private VariableSizedBufferSegment<int> _valueStore;

            internal override void Initialize(BTree<int> owner, ChunkBasedSegment segment, ChunkBasedSegmentAccessorPool pool)
            {
                base.Initialize(owner, segment, pool);
                _valueStore = new VariableSizedBufferSegment<int>(segment, pool);

            }

            public override int Append(int bufferId, int value) => _valueStore.AddElement(bufferId, value);
            public override VariableSizedBufferReadOnlyAccessor<int> GetBufferReadOnlyAccessor(int bufferId) => _valueStore.GetReadOnlyAccessor(bufferId);

            public override int CreateBuffer() => _valueStore.AllocateBuffer();

            public override int RemoveFromBuffer(int bufferId, int elementId, int value) => _valueStore.DeleteElement(bufferId, elementId, value);
            public override void DeleteBuffer(int bufferId) => _valueStore.DeleteBuffer(bufferId);
        }
    }

    public abstract class IntBTree : BTree<int>
    {
        unsafe public class IntNodeStorage : BaseNodeStorage
        {
            internal override void Initialize(BTree<int> owner, ChunkBasedSegment segment, ChunkBasedSegmentAccessorPool pool)
            {
                base.Initialize(owner, segment, pool);
                Debug.Assert(segment.Stride == sizeof(Index32Chunk));
            }

            #region Chunk Properties Access

            public override void InitializeNode(NodeWrapper node, NodeStates states)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                chunk.StateFlags = states;
            }
            public override int GetNodeCapacity() => Index32Chunk.Capacity;

            public override NodeWrapper GetLeftNode(NodeWrapper node)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                return new NodeWrapper(this, chunk.LeftValue);
            }

            public override void SetLeftNode(NodeWrapper node, int previousNodeId)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                chunk.LeftValue = previousNodeId;
            }

            public override NodeWrapper GetPreviousNode(NodeWrapper node)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                return new NodeWrapper(this, chunk.PrevChunk);
            }

            public override void SetPreviousNode(NodeWrapper node, int previousNodeId)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                chunk.PrevChunk = previousNodeId;
            }

            public override NodeWrapper GetNextNode(NodeWrapper node)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                return new NodeWrapper(this, chunk.NextChunk);
            }

            public override void SetNextNode(NodeWrapper node, int nextNodeId)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                chunk.NextChunk = nextNodeId;
            }

            public override KeyValueItem GetItem(NodeWrapper node, int index, bool adjust)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                var i = adjust ? Index32Chunk.Adjust(chunk.Start + index) : index;
                return new KeyValueItem(chunk.Keys[i], chunk.Values[i]);
            }

            public override void SetItem(NodeWrapper node, int index, KeyValueItem value, bool adjust)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                Set(ref chunk, index, value, adjust);
            }

            public override int GetCount(NodeWrapper node)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                return chunk.Count;
            }

            public override void SetCount(NodeWrapper node, int value)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                chunk.Count = value;
            }

            public override int GetStart(NodeWrapper node)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                return chunk.Start;
            }

            public override void SetStart(NodeWrapper node, int value)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                chunk.Start = value;
            }

            public override int GetEnd(NodeWrapper node)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                return Index32Chunk.Adjust(chunk.Start + chunk.Count);
            }

            public override NodeStates GetNodeStates(NodeWrapper node)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                return chunk.StateFlags;
            }
            
            #endregion

            #region Chunk Operations

            public override void PushFirst(NodeWrapper node, KeyValueItem item)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                
                DecrementStart(ref chunk);

                var start = chunk.Start;
                chunk.Keys[start] = item.Key;
                chunk.Values[start] = item.Value;

                ++chunk.Count;
            }

            public override void PushLast(NodeWrapper node, KeyValueItem item)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                var c = chunk.Count++;
                Set(ref chunk, c, item, true);
            }

            public override int Append(int bufferId, int value) => throw new Exception("Shouldn't be called as key replace is not supported and multi-value neither");

            public override void Insert(NodeWrapper node, int index, KeyValueItem item)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
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
                    RightShift(ref chunk, Index32Chunk.Adjust(chunk.Start + index), rsh); // move End to End+1
                    Set(ref chunk, index, item, true);
                }

                chunk.Count++;
            }

            public override int CreateBuffer() => default;

            public override VariableSizedBufferReadOnlyAccessor<int> GetBufferReadOnlyAccessor(int bufferId) => default;
            public override int RemoveFromBuffer(int bufferId, int elementId, int value) => default;
            public override void DeleteBuffer(int bufferId) { }

            public override NodeWrapper GetFirstChild(NodeWrapper node)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                return new NodeWrapper(this, chunk.LeftValue);
            }

            public override bool IsRotated(NodeWrapper node)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                return (chunk.Start + chunk.Count) > Index32Chunk.Capacity;
            }

            public override int BinarySearch(NodeWrapper node, int key, IComparer<int> comparer)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                fixed (int* keys = chunk.Keys)
                {
                    if (IsRotated(node))
                    {
                        if (comparer.Compare(key, chunk.Keys[Index32Chunk.Capacity - 1]) <= 0) // search right side if item is smaller than last item in array.
                        {
                            var find = BTreeExtensions.BinarySearch(keys, chunk.Start, Index32Chunk.Capacity - chunk.Start, key, comparer);
                            return find - chunk.Start * find.Sign();
                        }
                        else // search left side
                        {
                            var find = BTreeExtensions.BinarySearch(keys, 0, chunk.End, key, comparer);
                            return find + (Index32Chunk.Capacity - chunk.Start) * find.Sign();
                        }
                    }
                    else
                    {
                        var find = BTreeExtensions.BinarySearch(keys, chunk.Start, chunk.Count, key, comparer);
                        return find - chunk.Start * find.Sign();
                    }
                }
            }

            public override NodeWrapper SplitRight(NodeWrapper node, NodeStates states)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                return SplitRight(ref chunk, states);
            }

            public override KeyValueItem RemoveAt(NodeWrapper node, int index)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                var item = GetItem(node, index, true);

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
                    LeftShift(ref chunk, Index32Chunk.Adjust(chunk.Start + index + 1), lsh); // move End to End-1
                    Set(ref chunk, chunk.Count - 1, default, true); // remove last item
                }

                chunk.Count--;
                return item;
            }

            public override void MergeLeft(NodeWrapper left, NodeWrapper right)
            {
                ref var leftChunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(left.ChunkId);
                ref var rightChunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(right.ChunkId);

                var lk = leftChunk.KeysAsSpan;
                var lv = leftChunk.ValuesAsSpan;
                var rk = rightChunk.KeysAsSpan;
                var rv = rightChunk.ValuesAsSpan;

                if (leftChunk.Count + right.Count > Index32Chunk.Capacity)
                    throw new InvalidOperationException("can not merge, there is not enough capacity for this array.");

                var end = leftChunk.Start + leftChunk.Count;

                if (leftChunk.IsRotated)
                {
                    var start = end - Index32Chunk.Capacity;

                    if (!rightChunk.IsRotated)
                    {
                        rk.Slice(right.Start, right.Count).CopyTo(lk.Slice(start, right.Count));
                        rv.Slice(right.Start, right.Count).CopyTo(lv.Slice(start, right.Count));
                    }
                    else
                    {
                        var srLen = right.Capacity - right.Start; // right length
                        var slLen = right.Count - srLen; // left length (remaining)

                        rk.Slice(right.Start, srLen).CopyTo(lk.Slice(start, srLen));
                        rv.Slice(right.Start, srLen).CopyTo(lv.Slice(start, srLen));

                        rk.Slice(0, slLen).CopyTo(lk.Slice(start + srLen, slLen));
                        rv.Slice(0, slLen).CopyTo(lv.Slice(start + srLen, slLen));
                    }
                }
                else
                {
                    bool copyIsOnePiece = end + right.Count <= Index32Chunk.Capacity;

                    if (!rightChunk.IsRotated)
                    {
                        if (copyIsOnePiece)
                        {
                            rk.Slice(right.Start, right.Count).CopyTo(lk.Slice(end, right.Count));
                            rv.Slice(right.Start, right.Count).CopyTo(lv.Slice(end, right.Count));
                        }
                        else
                        {
                            var length = Index32Chunk.Capacity - end;
                            var remaining = right.Count - length;

                            rk.Slice(right.Start, length).CopyTo(lk.Slice(end, length));
                            rk.Slice(right.Start + length, remaining).CopyTo(lk.Slice(0, remaining));

                            rv.Slice(right.Start, length).CopyTo(lv.Slice(end, length));
                            rv.Slice(right.Start + length, remaining).CopyTo(lv.Slice(0, remaining));
                        }
                    }
                    else
                    {
                        var srLen = right.Capacity - right.Start; // right length
                        var slLen = right.Count - srLen; // left length (remaining)

                        if (copyIsOnePiece)
                        {
                            rk.Slice(right.Start, srLen).CopyTo(lk.Slice(end, srLen));
                            rk.Slice(0, slLen).CopyTo(lk.Slice(end + srLen, slLen));

                            rv.Slice(right.Start, srLen).CopyTo(lv.Slice(end, srLen));
                            rv.Slice(0, slLen).CopyTo(lv.Slice(end + srLen, slLen));
                        }
                        else
                        {
                            var mergeEnd = end + srLen;

                            if (mergeEnd <= Index32Chunk.Capacity)
                            {
                                var secondCopyFirstLength = Index32Chunk.Capacity - mergeEnd;
                                var secondCopySecondLength = slLen - secondCopyFirstLength;

                                rk.Slice(right.Start, srLen).CopyTo(lk.Slice(end, srLen));
                                rv.Slice(right.Start, srLen).CopyTo(lv.Slice(end, srLen));

                                rk.Slice(0, secondCopySecondLength).CopyTo(lk.Slice(mergeEnd, secondCopyFirstLength));
                                rv.Slice(0, secondCopySecondLength).CopyTo(lv.Slice(mergeEnd, secondCopyFirstLength));
                                rk.Slice(secondCopyFirstLength, secondCopySecondLength).CopyTo(lk.Slice(0, secondCopySecondLength));
                                rv.Slice(secondCopyFirstLength, secondCopySecondLength).CopyTo(lv.Slice(0, secondCopySecondLength));
                            }
                            else
                            {
                                var firstCopyFirstLength = Index32Chunk.Capacity - end;
                                var firstCopySecondLength = srLen - firstCopyFirstLength;
                                var firstCopySecondStart = right.Start + firstCopyFirstLength;

                                rk.Slice(right.Start, firstCopyFirstLength).CopyTo(lk.Slice(end, firstCopyFirstLength));
                                rk.Slice(firstCopySecondStart, firstCopySecondLength).CopyTo(lk.Slice(0, firstCopySecondLength));
                                rv.Slice(right.Start, firstCopyFirstLength).CopyTo(lv.Slice(end, firstCopyFirstLength));
                                rv.Slice(firstCopySecondStart, firstCopySecondLength).CopyTo(lv.Slice(0, firstCopySecondLength));

                                rk.Slice(0, slLen).CopyTo(lk.Slice(firstCopySecondLength, slLen));
                                rv.Slice(0, slLen).CopyTo(lv.Slice(firstCopySecondLength, slLen));
                            }
                        }
                    }
                }

                leftChunk.Count += right.Count; // correct array length.
            }

            public override NodeWrapper GetLastChild(NodeWrapper node)
            {
                ref readonly var chunk = ref SegmentAccessorPool.RO.GetChunk<Index32Chunk>(node.ChunkId);
                var index = Index32Chunk.Adjust(chunk.Start + chunk.Count - 1);
                return new NodeWrapper(this, chunk.Values[index]);
            }

            public override void IncrementStart(NodeWrapper node)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                IncrementStart(ref chunk);
            }

            public override void DecrementStart(NodeWrapper node)
            {
                ref var chunk = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(node.ChunkId);
                DecrementStart(ref chunk);
            }

            #endregion

            #region Chunk Direct Access Wrappers

            //private NodeWrapper GetPreviousNode(ref Index32Chunk chunk) => new(this, chunk.PrevChunk);
            //private NodeWrapper GetNextNode(ref Index32Chunk chunk) => new(this, chunk.NextChunk);
            //private NodeWrapper GetLeftNode(ref Index32Chunk chunk) => new(this, chunk.LeftValue);
            //private void SetPreviousNode(ref Index32Chunk chunk, NodeWrapper value) => chunk.PrevChunk = value.ChunkId;
            //private void SetNextNode(ref Index32Chunk chunk, NodeWrapper value) => chunk.NextChunk = value.ChunkId;
            //private void SetLeftNode(ref Index32Chunk chunk, NodeWrapper value) => chunk.LeftValue = value.ChunkId;

            private static void Set(ref Index32Chunk chunk, int index, KeyValueItem item, bool adjust)
            {
                var i = adjust ? Index32Chunk.Adjust(chunk.Start + index) : index;
                chunk.Keys[i] = item.Key;
                chunk.Values[i] = item.Value;
            }

            private static void DecrementStart(ref Index32Chunk chunk)
            {
                if (chunk.Start == 0)
                {
                    chunk.Start = Index32Chunk.Capacity - 1;
                }
                else
                {
                    --chunk.Start;
                }
            }

            private static void IncrementStart(ref Index32Chunk chunk)
            {
                if (chunk.Start == (Index32Chunk.Capacity - 1))
                {
                    chunk.Start = 0;
                }
                else
                {
                    ++chunk.Start;
                }
            }

            private void LeftShift(ref Index32Chunk chunk, int index, int length)
            {
                if (length == 0) return;
                if (length < 0 || length > Index32Chunk.Capacity) throw new ArgumentOutOfRangeException(nameof(length));
                if (index < 0  || index >= Index32Chunk.Capacity) throw new ArgumentOutOfRangeException(nameof(length));

                var k = chunk.KeysAsSpan;
                var v = chunk.ValuesAsSpan;

                if (index == 0)
                {
                    var first = k[0];
                    k.Slice(1, length - 1).CopyTo(k);
                    k[^1] = first;
                    
                    first = v[0];
                    v.Slice(1, length - 1).CopyTo(v);
                    v[^1] = first;
                }
                else if (index + length > k.Length)
                {
                    var l = index + length - k.Length - 1;
                    var remaining = length - l - 1;
                    var first = k[0];
                    k.Slice(1, l).CopyTo(k.Slice(0, l));
                    k.Slice(index, remaining).CopyTo(k.Slice(index - 1, remaining));
                    k[^1] = first;
                    
                    first = v[0];
                    v.Slice(1, l).CopyTo(v.Slice(0, l));
                    v.Slice(index, remaining).CopyTo(v.Slice(index - 1, remaining));
                    v[^1] = first;
                }
                else
                {
                    k.Slice(index, length).CopyTo(k.Slice(index - 1, length));
                    v.Slice(index, length).CopyTo(v.Slice(index - 1, length));
                }
            }

            private void RightShift(ref Index32Chunk chunk, int index, int length)
            {
                if (length == 0) return;
                if (length < 0 || length > Index32Chunk.Capacity) throw new ArgumentOutOfRangeException(nameof(length));
                if (index < 0  || index >= Index32Chunk.Capacity) throw new ArgumentOutOfRangeException(nameof(length));

                var k = chunk.KeysAsSpan;
                var v = chunk.ValuesAsSpan;

                var lastInd = Index32Chunk.Capacity - 1;
                if (index + length > lastInd) // if overflows, rotate.
                {
                    var last = k[lastInd];
                    var rl = lastInd - index;
                    var remaining = length - rl - 1;
                    k.Slice(index, rl).CopyTo(k.Slice(index + 1, rl));
                    k.Slice(0, remaining).CopyTo(k.Slice(1, remaining));
                    k[0] = last;
                    
                    last = v[lastInd];
                    v.Slice(index, rl).CopyTo(v.Slice(index + 1, rl));
                    v.Slice(0, remaining).CopyTo(v.Slice(1, remaining));
                    v[0] = last;
                }
                else
                {
                    k.Slice(index, length).CopyTo(k.Slice(index + 1, length));
                    v.Slice(index, length).CopyTo(v.Slice(index + 1, length));
                }
            }

            public NodeWrapper SplitRight(ref Index32Chunk left, NodeStates states)
            {
                var rightNode = Owner.AllocNode(states);
                ref var right = ref SegmentAccessorPool.RW.GetChunk<Index32Chunk>(rightNode.ChunkId);

                var lr = left.Count / 2; // length of right side
                var lrc = 1 + ((left.Count - 1) / 2); // length of right (ceiling of Length/2)
                var sr = Index32Chunk.Adjust(left.Start + lrc); // start of right side

                right.Count = lr;
                left.Count -= right.Count;

                var lk = left.KeysAsSpan;
                var lv = left.ValuesAsSpan;
                var rk = right.KeysAsSpan;
                var rv = right.ValuesAsSpan;

                var capacity = Index32Chunk.Capacity;

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

        protected override BaseNodeStorage GetStorage() => new IntNodeStorage();

        protected IntBTree(ChunkBasedSegment segment, ChunkBasedSegmentAccessorPool pool) : base(segment, pool)
        {
        }
    }
}