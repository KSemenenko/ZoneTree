﻿namespace Tenray.ZoneTree.Core;

using System.Collections.Concurrent;
using System.Collections.Generic;
using Tenray.ZoneTree.Collections;

/// <summary>
/// The <see cref="Replicator{TKey, TValue}"/> class provides asynchronous replication for 
/// <see cref="IZoneTree{TKey, TValue}"/> instances, allowing efficient upsert operations 
/// and background maintenance tasks.
/// </summary>
/// <typeparam name="TKey">The type of the key used in the ZoneTree.</typeparam>
/// <typeparam name="TValue">The type of the value used in the ZoneTree.</typeparam>
public sealed class Replicator<TKey, TValue> : IDisposable
{
    /// <summary>
    /// The main <see cref="IZoneTree{TKey, TValue}"/> instance that acts as the replica.
    /// This holds the replicated data.
    /// </summary>
    public readonly IZoneTree<TKey, TValue> Replica;

    /// <summary>
    /// An <see cref="IZoneTree{TKey, long}"/> that tracks the latest operation indexes for each key.
    /// This helps in ensuring that only the most recent updates are reflected in the <see cref="Replica"/>.
    /// </summary>
    public readonly IZoneTree<TKey, long> LatestOpIndexes;

    /// <summary>
    /// The <see cref="IMaintainer"/> responsible for managing background maintenance jobs,
    /// such as cleaning inactive caches and evicting data to disk.
    /// </summary>
    public readonly IMaintainer Maintainer;

    /// <summary>
    /// A flag indicating whether data should be evicted to disk when the replicator is disposed.
    /// </summary>
    readonly bool EvictToDiskOnDispose;

    /// <summary>
    /// A flag indicating whether the replicator has already been disposed to avoid multiple disposal operations.
    /// </summary>
    bool isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="Replicator{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="replica">The <see cref="IZoneTree{TKey, TValue}"/> instance representing the replica.</param>
    /// <param name="dataPath">The file path where data will be persisted to disk.</param>
    /// <param name="evictToDiskOnDispose">Indicates whether the data should be evicted to disk on dispose.</param>
    /// <param name="configure">
    /// An optional action to configure the <see cref="ZoneTreeFactory{TKey, long}"/> instance used for 
    /// creating or opening the <see cref="LatestOpIndexes"/> tree.
    /// </param>
    public Replicator(
        IZoneTree<TKey, TValue> replica,
        string dataPath,
        bool evictToDiskOnDispose = true,
        Action<ZoneTreeFactory<TKey, long>> configure = null)
    {
        this.Replica = replica;
        EvictToDiskOnDispose = evictToDiskOnDispose;
        var factory = new ZoneTreeFactory<TKey, long>()
            .SetDataDirectory(dataPath);
        if (configure != null) configure(factory);
        LatestOpIndexes = factory.OpenOrCreate();
        Maintainer = LatestOpIndexes.CreateMaintainer();
        Maintainer.EnableJobForCleaningInactiveCaches = true;
    }

    /// <summary>
    /// Handles the upsert operation in the replicator, ensuring atomic updates 
    /// to both the <see cref="LatestOpIndexes"/> and the <see cref="Replica"/>.
    /// </summary>
    /// <param name="key">The key of the element to be upserted.</param>
    /// <param name="value">The value of the element to be upserted.</param>
    /// <param name="opIndex">The operation index associated with this upsert operation.</param>
    /// <remarks>
    /// The upsert operation ensures that the <see cref="LatestOpIndexes"/> is updated atomically.
    /// If the new operation index (<paramref name="opIndex"/>) is greater than or equal to the existing index,
    /// the key-value pair is upserted into the <see cref="Replica"/>.
    /// </remarks>
    public void OnUpsert(TKey key, TValue value, long opIndex)
    {
        LatestOpIndexes.TryAtomicAddOrUpdate(
                key,
                (ref long newOpIndex) =>
                {
                    newOpIndex = opIndex;
                    return true;
                },
                (ref long existingOpIndex) =>
                {
                    if (opIndex < existingOpIndex)
                        return false;
                    existingOpIndex = opIndex;
                    return true;
                },
                (in long _, long _, OperationResult result) =>
                {
                    if (result == OperationResult.Cancelled) return;
                    Replica.Upsert(key, value);
                });
    }

    /// <summary>
    /// Releases all resources used by the <see cref="Replicator{TKey, TValue}"/>.
    /// </summary>
    /// <remarks>
    /// If <see cref="EvictToDiskOnDispose"/> is set to <c>true</c>, the data is 
    /// evicted to disk before disposal. The method ensures that all background 
    /// maintenance jobs are completed and disposes of the <see cref="LatestOpIndexes"/> 
    /// and the <see cref="Maintainer"/>.
    /// </remarks>
    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        if (EvictToDiskOnDispose)
            Maintainer.EvictToDisk();
        Maintainer.WaitForBackgroundThreads();
        Maintainer.Dispose();
        LatestOpIndexes.Dispose();
    }
}
