﻿using Tenray.ZoneTree.Collections;
using Tenray.ZoneTree.Collections.BTree;
using Tenray.ZoneTree.Exceptions;
using Tenray.ZoneTree.Segments;

namespace Tenray.ZoneTree.Core;

public sealed partial class ZoneTree<TKey, TValue> : IZoneTree<TKey, TValue>, IZoneTreeMaintenance<TKey, TValue>
{
    public bool ContainsKey(in TKey key)
    {
        if (MutableSegment.ContainsKey(key))
        {
            if (MutableSegment.TryGet(key, out TValue value))
                return !IsDeleted(key, value);
        }

        return TryGetFromReadonlySegments(in key, out _);
    }

    bool TryGetFromReadonlySegments(in TKey key, out TValue value)
    {
        foreach (var segment in ReadOnlySegmentQueue)
        {
            if (segment.TryGet(key, out value))
            {
                return !IsDeleted(key, value);
            }
        }

        while (true)
        {
            try
            {
                if (DiskSegment.TryGet(key, out value))
                {
                    return !IsDeleted(key, value);
                }

                foreach (var segment in BottomSegmentQueue)
                {
                    if (segment.TryGet(key, out value))
                    {
                        return !IsDeleted(key, value);
                    }
                }
                return false;
            }
            catch (DiskSegmentIsDroppingException)
            {
                continue;
            }
        }
    }

    public bool TryGet(in TKey key, out TValue value)
    {
        if (MutableSegment.TryGet(key, out value))
        {
            return !IsDeleted(key, value);
        }
        return TryGetFromReadonlySegments(in key, out value);
    }

    public bool TryAdd(in TKey key, in TValue value, out long opIndex)
    {
        if (ContainsKey(key))
        {
            opIndex = 0;
            return false;
        }
        opIndex = Upsert(in key, in value);
        return true;
    }

    public bool TryGetAndUpdate(
        in TKey key,
        out TValue value,
        ValueUpdaterDelegate<TValue> valueUpdater,
        out long opIndex)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();

        if (MutableSegment.TryGet(key, out value))
        {
            if (IsDeleted(key, value))
            {
                opIndex = 0;
                return false;
            }
        }
        else if (!TryGetFromReadonlySegments(in key, out value))
        {
            opIndex = 0;
            return false;
        }

        if (!valueUpdater(ref value))
        {
            // return true because
            // no update happened, but the value is found.
            opIndex = 0;
            return true;
        }
        opIndex = Upsert(in key, in value);
        return true;
    }

    public bool TryAtomicGetAndUpdate(
        in TKey key,
        out TValue value,
        ValueUpdaterDelegate<TValue> valueUpdater)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();

        lock (AtomicUpdateLock)
        {
            if (MutableSegment.TryGet(key, out value))
            {
                if (IsDeleted(key, value))
                    return false;
            }
            else if (!TryGetFromReadonlySegments(in key, out value))
                return false;

            if (!valueUpdater(ref value))
            {
                // return true because
                // no update happened, but the value is found.
                return true;
            }

            Upsert(in key, in value);
            return true;
        }
    }

    public bool TryAtomicAdd(in TKey key, in TValue value, out long opIndex)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();

        lock (AtomicUpdateLock)
        {
            if (ContainsKey(key))
            {
                opIndex = 0;
                return false;
            }
            opIndex = Upsert(key, value);
            return true;
        }
    }

    public bool TryAtomicUpdate(in TKey key, in TValue value, out long opIndex)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();

        lock (AtomicUpdateLock)
        {
            if (!ContainsKey(key))
            {
                opIndex = 0;
                return false;
            }
            opIndex = Upsert(key, value);
            return true;
        }
    }

    static OperationResultDelegate<TValue> EmptyOperationResultDelegate =
        (in TValue value, long opIndex, OperationResult result) =>
        {
        };

    public bool TryAtomicAddOrUpdate(
        in TKey key,
        in TValue valueToAdd,
        ValueUpdaterDelegate<TValue> valueUpdater,
        OperationResultDelegate<TValue> result)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();
        AddOrUpdateResult status;
        IMutableSegment<TKey, TValue> mutableSegment;
        var opIndex = 0L;
        if (result == null)
            result = EmptyOperationResultDelegate;

        while (true)
        {
            lock (AtomicUpdateLock)
            {
                mutableSegment = MutableSegment;
                if (mutableSegment.IsFrozen)
                {
                    status = AddOrUpdateResult.RETRY_SEGMENT_IS_FULL;
                }
                else if (mutableSegment.TryGet(in key, out var existing))
                {
                    if (!valueUpdater(ref existing))
                    {
                        result(in existing, 0, OperationResult.Cancelled);
                        return false;
                    }
                    status = mutableSegment.Upsert(key, existing, out opIndex);
                    if (status == AddOrUpdateResult.UPDATED)
                    {
                        result(in existing, opIndex, OperationResult.Updated);
                        return false;
                    }
                }
                else if (TryGetFromReadonlySegments(in key, out existing))
                {
                    if (!valueUpdater(ref existing))
                    {
                        result(in existing, 0, OperationResult.Cancelled);
                        return false;
                    }
                    status = mutableSegment.Upsert(key, existing, out opIndex);
                    if (status == AddOrUpdateResult.ADDED)
                    {
                        result(in existing, opIndex, OperationResult.Updated);
                        return false;
                    }
                }
                else
                {
                    status = mutableSegment.Upsert(key, valueToAdd, out opIndex);
                    if (status == AddOrUpdateResult.ADDED)
                    {
                        result(in existing, opIndex, OperationResult.Added);
                        return true;
                    }
                }
            }
            switch (status)
            {
                case AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN:
                    continue;
                case AddOrUpdateResult.RETRY_SEGMENT_IS_FULL:
                    MoveMutableSegmentForward(mutableSegment);
                    continue;
                default:
                    throw new Exception("Impossible.");
            }
        }
    }

    public bool TryAtomicAddOrUpdate(
        in TKey key,
        ValueAdderDelegate<TValue> valueAdder,
        ValueUpdaterDelegate<TValue> valueUpdater,
        OperationResultDelegate<TValue> result)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();
        AddOrUpdateResult status;
        IMutableSegment<TKey, TValue> mutableSegment;
        var opIndex = 0L;
        if (result == null)
            result = EmptyOperationResultDelegate;
        while (true)
        {
            lock (AtomicUpdateLock)
            {
                mutableSegment = MutableSegment;
                if (mutableSegment.IsFrozen)
                {
                    status = AddOrUpdateResult.RETRY_SEGMENT_IS_FULL;
                }
                else if (mutableSegment.TryGet(in key, out var existing))
                {
                    if (!valueUpdater(ref existing))
                    {
                        result(in existing, 0, OperationResult.Cancelled);
                        return false;
                    }
                    status = mutableSegment.Upsert(key, existing, out opIndex);
                    if (status == AddOrUpdateResult.UPDATED)
                    {
                        result(in existing, opIndex, OperationResult.Updated);
                        return false;
                    }
                }
                else if (TryGetFromReadonlySegments(in key, out existing))
                {
                    if (!valueUpdater(ref existing))
                    {
                        result(in existing, 0, OperationResult.Cancelled);
                        return false;
                    }
                    status = mutableSegment.Upsert(key, existing, out opIndex);
                    if (status == AddOrUpdateResult.ADDED)
                    {
                        result(in existing, opIndex, OperationResult.Updated);
                        return false;
                    }
                }
                else
                {
                    if (!valueAdder(ref existing))
                    {
                        result(in existing, 0, OperationResult.Cancelled);
                        return false;
                    }
                    status = mutableSegment.Upsert(key, existing, out opIndex);
                    if (status == AddOrUpdateResult.ADDED)
                    {
                        result(in existing, opIndex, OperationResult.Added);
                        return true;
                    }
                }
            }
            switch (status)
            {
                case AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN:
                    continue;
                case AddOrUpdateResult.RETRY_SEGMENT_IS_FULL:
                    MoveMutableSegmentForward(mutableSegment);
                    continue;
                default:
                    throw new Exception("Impossible.");
            }
        }
    }

    public long AtomicUpsert(in TKey key, in TValue value)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();
        lock (AtomicUpdateLock)
        {
            return Upsert(in key, in value);
        }
    }

    public long Upsert(in TKey key, in TValue value)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();
        while (true)
        {
            var mutableSegment = MutableSegment;
            var status = mutableSegment.Upsert(key, value, out var opIndex);
            switch (status)
            {
                case AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN:
                    continue;
                case AddOrUpdateResult.RETRY_SEGMENT_IS_FULL:
                    MoveMutableSegmentForward(mutableSegment);
                    continue;
                default:
                    return opIndex;
            }
        }
    }

    public long Upsert(in TKey key, GetValueDelegate<TKey, TValue> valueGetter)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();
        while (true)
        {
            var mutableSegment = MutableSegment;
            var status = mutableSegment.Upsert(key, valueGetter, out var opIndex);
            switch (status)
            {
                case AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN:
                    continue;
                case AddOrUpdateResult.RETRY_SEGMENT_IS_FULL:
                    MoveMutableSegmentForward(mutableSegment);
                    continue;
                default:
                    return opIndex;
            }
        }
    }

    public bool TryDelete(in TKey key, out long opIndex)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();
        if (!ContainsKey(key))
        {
            opIndex = 0;
            return false;
        }
        opIndex = ForceDelete(in key);
        return true;
    }

    public long ForceDelete(in TKey key)
    {
        if (IsReadOnly)
            throw new ZoneTreeIsReadOnlyException();
        long opIndex;
        while (true)
        {
            var mutableSegment = MutableSegment;
            var status = mutableSegment.Delete(key, out opIndex);
            switch (status)
            {
                case AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN:
                    continue;
                case AddOrUpdateResult.RETRY_SEGMENT_IS_FULL:
                    MoveMutableSegmentForward(mutableSegment);
                    continue;
                default: return opIndex;
            }
        }
    }
}
