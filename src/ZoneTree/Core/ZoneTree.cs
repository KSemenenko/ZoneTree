﻿using System.Collections.Concurrent;
using Tenray.ZoneTree.Collections;
using Tenray.ZoneTree.Comparers;
using Tenray.ZoneTree.Logger;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree.Segments;
using Tenray.ZoneTree.Segments.InMemory;
using Tenray.ZoneTree.Segments.NullDisk;
using Tenray.ZoneTree.Serializers;

namespace Tenray.ZoneTree.Core;

public sealed partial class ZoneTree<TKey, TValue> : IZoneTree<TKey, TValue>, IZoneTreeMaintenance<TKey, TValue>
{
    public const string SegmentWalCategory = "seg";

    public ILogger Logger { get; }

    readonly ZoneTreeMeta ZoneTreeMeta = new();

    readonly ZoneTreeMetaWAL<TKey, TValue> MetaWal;

    readonly ZoneTreeOptions<TKey, TValue> Options;

    readonly MinHeapEntryRefComparer<TKey, TValue> MinHeapEntryComparer;

    readonly MaxHeapEntryRefComparer<TKey, TValue> MaxHeapEntryComparer;

    readonly IsDeletedDelegate<TKey, TValue> IsDeleted;

    readonly SingleProducerSingleConsumerQueue<IReadOnlySegment<TKey, TValue>> ReadOnlySegmentQueue = new();

    volatile SingleProducerSingleConsumerQueue<IDiskSegment<TKey, TValue>> BottomSegmentQueue = new();

    readonly IIncrementalIdProvider IncrementalIdProvider = new IncrementalIdProvider();

    readonly object AtomicUpdateLock = new();

    readonly object LongMergerLock = new();

    readonly object LongBottomSegmentsMergerLock = new();

    readonly object ShortMergerLock = new();

    volatile bool IsMergingFlag;

    volatile bool IsBottomSegmentsMergingFlag;

    volatile bool IsCancelMergeRequested;

    volatile bool IsCancelBottomSegmentsMergeRequested;

    volatile IMutableSegment<TKey, TValue> _mutableSegment;

    public IMutableSegment<TKey, TValue> MutableSegment { get => _mutableSegment; private set => _mutableSegment = value; }

    public IReadOnlyList<IReadOnlySegment<TKey, TValue>> ReadOnlySegments =>
        ReadOnlySegmentQueue.ToLastInFirstArray();

    volatile IDiskSegment<TKey, TValue> _diskSegment = new NullDiskSegment<TKey, TValue>();

    public IDiskSegment<TKey, TValue> DiskSegment { get => _diskSegment; private set => _diskSegment = value; }

    public IReadOnlyList<IDiskSegment<TKey, TValue>> BottomSegments =>
        BottomSegmentQueue.ToLastInFirstArray();

    public bool IsMerging { get => IsMergingFlag; private set => IsMergingFlag = value; }

    public bool IsBottomSegmentsMerging { get => IsBottomSegmentsMergingFlag; private set => IsBottomSegmentsMergingFlag = value; }

    public int ReadOnlySegmentsCount => ReadOnlySegmentQueue.Length;

    public long ReadOnlySegmentsRecordCount => ReadOnlySegmentQueue.Sum(x => x.Length);

    public long MutableSegmentRecordCount => MutableSegment.Length;

    public long InMemoryRecordCount
    {
        get
        {
            lock (AtomicUpdateLock)
            {
                return MutableSegment.Length + ReadOnlySegmentsRecordCount;
            }
        }
    }

    public long TotalRecordCount
    {
        get
        {
            lock (ShortMergerLock)
            {
                return InMemoryRecordCount +
                    DiskSegment.Length +
                    BottomSegmentQueue.Sum(x => x.Length);
            }
        }
    }

    public IZoneTreeMaintenance<TKey, TValue> Maintenance => this;

    public event MutableSegmentMovedForward<TKey, TValue> OnMutableSegmentMovedForward;

    public event MergeOperationStarted<TKey, TValue> OnMergeOperationStarted;

    public event MergeOperationEnded<TKey, TValue> OnMergeOperationEnded;

    public event BottomSegmentsMergeOperationStarted<TKey, TValue> OnBottomSegmentsMergeOperationStarted;

    public event BottomSegmentsMergeOperationEnded<TKey, TValue> OnBottomSegmentsMergeOperationEnded;

    public event DiskSegmentCreated<TKey, TValue> OnDiskSegmentCreated;

    public event DiskSegmentCreated<TKey, TValue> OnDiskSegmentActivated;

    public event CanNotDropReadOnlySegment<TKey, TValue> OnCanNotDropReadOnlySegment;

    public event CanNotDropDiskSegment<TKey, TValue> OnCanNotDropDiskSegment;

    public event CanNotDropDiskSegmentCreator<TKey, TValue> OnCanNotDropDiskSegmentCreator;

    public event ZoneTreeIsDisposing<TKey, TValue> OnZoneTreeIsDisposing;

    volatile bool _isReadOnly;

    public bool IsReadOnly { get => _isReadOnly; set => _isReadOnly = value; }

    public IRefComparer<TKey> Comparer => Options.Comparer;

    public ISerializer<TKey> KeySerializer => Options.KeySerializer;

    public ISerializer<TValue> ValueSerializer => Options.ValueSerializer;

    public ZoneTree(ZoneTreeOptions<TKey, TValue> options)
    {
        Logger = options.Logger;
        options.WriteAheadLogProvider.InitCategory(SegmentWalCategory);
        MetaWal = new ZoneTreeMetaWAL<TKey, TValue>(options, false);
        Options = options;
        MinHeapEntryComparer = new MinHeapEntryRefComparer<TKey, TValue>(options.Comparer);
        MaxHeapEntryComparer = new MaxHeapEntryRefComparer<TKey, TValue>(options.Comparer);
        MutableSegment = new MutableSegment<TKey, TValue>(
            options, IncrementalIdProvider.NextId(), new IncrementalIdProvider());
        IsDeleted = options.IsDeleted;
        FillZoneTreeMeta();
        ZoneTreeMeta.MaximumOpIndex = MutableSegment.OpIndexProvider.LastId;
        MetaWal.SaveMetaData(
            ZoneTreeMeta,
            MutableSegment.SegmentId,
            DiskSegment.SegmentId,
            Array.Empty<long>(),
            Array.Empty<long>(),
            true);
    }

    public ZoneTree(
        ZoneTreeOptions<TKey, TValue> options,
        ZoneTreeMeta meta,
        IReadOnlyList<IReadOnlySegment<TKey, TValue>> readOnlySegments,
        IMutableSegment<TKey, TValue> mutableSegment,
        IDiskSegment<TKey, TValue> diskSegment,
        IReadOnlyList<IDiskSegment<TKey, TValue>> bottomSegments,
        long maximumSegmentId
        )
    {
        Logger = options.Logger;
        options.WriteAheadLogProvider.InitCategory(SegmentWalCategory);
        IncrementalIdProvider.SetNextId(maximumSegmentId + 1);
        MetaWal = new ZoneTreeMetaWAL<TKey, TValue>(options, false);
        ZoneTreeMeta = meta;
        Options = options;
        MinHeapEntryComparer = new MinHeapEntryRefComparer<TKey, TValue>(options.Comparer);
        MaxHeapEntryComparer = new MaxHeapEntryRefComparer<TKey, TValue>(options.Comparer);
        MutableSegment = mutableSegment;
        DiskSegment = diskSegment;
        DiskSegment.DropFailureReporter = ReportDropFailure;
        foreach (var ros in readOnlySegments.Reverse())
            ReadOnlySegmentQueue.Enqueue(ros);
        foreach (var bs in bottomSegments.Reverse())
            BottomSegmentQueue.Enqueue(bs);
        IsDeleted = options.IsDeleted;
    }

    void FillZoneTreeMeta()
    {
        if (MutableSegment != null)
            ZoneTreeMeta.MutableSegment = MutableSegment.SegmentId;
        ZoneTreeMeta.ComparerType = Options.Comparer.GetType().SimplifiedFullName();
        ZoneTreeMeta.KeyType = typeof(TKey).SimplifiedFullName();
        ZoneTreeMeta.ValueType = typeof(TValue).SimplifiedFullName();
        ZoneTreeMeta.KeySerializerType = Options.KeySerializer.GetType().SimplifiedFullName();
        ZoneTreeMeta.ValueSerializerType = Options.ValueSerializer.GetType().SimplifiedFullName();
        ZoneTreeMeta.DiskSegment = DiskSegment.SegmentId;
        ZoneTreeMeta.ReadOnlySegments = ReadOnlySegmentQueue.Select(x => x.SegmentId).ToArray();
        ZoneTreeMeta.BottomSegments = BottomSegmentQueue.Select(x => x.SegmentId).ToArray();
        ZoneTreeMeta.MutableSegmentMaxItemCount = Options.MutableSegmentMaxItemCount;
        ZoneTreeMeta.DiskSegmentMaxItemCount = Options.DiskSegmentMaxItemCount;
        ZoneTreeMeta.WriteAheadLogOptions = Options.WriteAheadLogOptions;
        ZoneTreeMeta.DiskSegmentOptions = Options.DiskSegmentOptions;
    }

    void ReportDropFailure(IDiskSegment<TKey, TValue> ds, Exception e)
    {
        OnCanNotDropDiskSegment?.Invoke(ds, e);
    }

    public void SaveMetaData()
    {
        lock (ShortMergerLock)
            lock (AtomicUpdateLock)
            {
                ZoneTreeMeta.MaximumOpIndex = MutableSegment.OpIndexProvider.LastId;
                MetaWal.SaveMetaData(
                    ZoneTreeMeta,
                    MutableSegment.SegmentId,
                    DiskSegment.SegmentId,
                    ReadOnlySegmentQueue.Select(x => x.SegmentId).ToArray(),
                    BottomSegmentQueue.Select(x => x.SegmentId).ToArray());
            }
    }

    public int ReleaseReadBuffers(long ticks)
    {
        var total = 0;
        if (DiskSegment != null) total += DiskSegment.ReleaseReadBuffers(ticks);
        foreach (var bs in BottomSegments)
        {
            total += bs.ReleaseReadBuffers(ticks);
        }
        return total;
    }

    public int ReleaseCircularKeyCacheRecords()
    {
        var total = 0;
        if (DiskSegment != null) total += DiskSegment.ReleaseCircularKeyCacheRecords();
        foreach (var bs in BottomSegments)
        {
            total += bs.ReleaseCircularKeyCacheRecords();
        }
        return total;
    }

    public int ReleaseCircularValueCacheRecords()
    {
        var total = 0;
        if (DiskSegment != null) total += DiskSegment.ReleaseCircularValueCacheRecords();
        foreach (var bs in BottomSegments)
        {
            total += bs.ReleaseCircularValueCacheRecords();
        }
        return total;
    }


    public void Dispose()
    {
        OnZoneTreeIsDisposing?.Invoke(this);
        MutableSegment.ReleaseResources();
        _diskSegment.Dispose();
        MetaWal.Dispose();
        foreach (var ros in ReadOnlySegments)
            ros.ReleaseResources();
        foreach (var bs in BottomSegmentQueue)
            bs.ReleaseResources();
    }

    public void Drop()
    {
        MetaWal.Dispose();
        MutableSegment.Drop();
        DiskSegment.Drop();
        DiskSegment.Dispose();
        foreach (var ros in ReadOnlySegmentQueue)
            ros.Drop();
        foreach (var bs in BottomSegmentQueue)
            bs.Drop();
        Options.WriteAheadLogProvider.DropStore();
        Options.RandomAccessDeviceManager.DropStore();
    }

    public long Count()
    {
        using var iterator = CreateInMemorySegmentsIterator(
            autoRefresh: false,
            includeDeletedRecords: true);
        IDiskSegment<TKey, TValue> diskSegment = null;
        try
        {
            lock (ShortMergerLock)
                lock (AtomicUpdateLock)
                {
                    // 2 things to synchronize with
                    // MoveSegmentForward and disk merger segment swap.
                    diskSegment = DiskSegment;

                    // ShortMergerLock ensures the diskSegment drop is not requested at the moment.
                    // We can safely attach an iterator to the disk segment.
                    diskSegment.AttachIterator();
                    iterator.Refresh();
                }

            if (!BottomSegmentQueue.IsEmpty)
                return CountFullScan();
            var count = diskSegment.Length;
            while (iterator.Next())
            {
                var key = iterator.CurrentKey;
                var hasKey = diskSegment.ContainsKey(key);
                var isDeleted = IsDeleted(key, iterator.CurrentValue);
                if (hasKey)
                {
                    if (isDeleted)
                        --count;
                }
                else
                {
                    if (!isDeleted)
                        ++count;
                }
            }
            return count;
        }
        finally
        {
            diskSegment.DetachIterator();
        }
    }

    public long CountFullScan()
    {
        using var iterator = CreateIterator(IteratorType.NoRefresh, false, false);
        var count = 0;
        while (iterator.Next())
            ++count;
        return count;
    }

    public IMaintainer CreateMaintainer()
    {
        return new ZoneTreeMaintainer<TKey, TValue>(this);
    }
}
