﻿using Tenray;
using Tenray.Collections;
using Tenray.WAL;
using ZoneTree.Core;
using ZoneTree.WAL;

namespace ZoneTree.Collections
{
    /// <summary>
    /// Persistent Dictionary implementation that is combined 
    /// with a WriteAheadLog.
    /// This class is not thread-safe.
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    public sealed class DictionaryWithWAL<TKey, TValue> : IDisposable
    {
        readonly int SegmentId;

        readonly string Category;

        readonly IWriteAheadLogProvider WriteAheadLogProvider;

        readonly IWriteAheadLog<TKey, TValue> WriteAheadLog;

        readonly IRefComparer<TKey> Comparer;

        readonly IsValueDeletedDelegate<TValue> IsValueDeleted;

        readonly MarkValueDeletedDelegate<TValue> MarkValueDeleted;

        Dictionary<TKey, TValue> Dictionary = new();

        public DictionaryWithWAL(
            int segmentId,
            string category,
            IWriteAheadLogProvider writeAheadLogProvider,
            ISerializer<TKey> keySerializer,
            ISerializer<TValue> valueSerializer,
            IRefComparer<TKey> comparer,
            IsValueDeletedDelegate<TValue> isValueDeleted,
            MarkValueDeletedDelegate<TValue> markValueDeleted)
        {
            WriteAheadLogProvider = writeAheadLogProvider;
            Comparer = comparer;
            WriteAheadLog = writeAheadLogProvider
                .GetOrCreateWAL(segmentId, keySerializer, valueSerializer);
            SegmentId = segmentId;
            Category = category;
            IsValueDeleted = isValueDeleted;
            MarkValueDeleted = markValueDeleted;
            LoadFromWriteAheadLog();
        }

        void LoadFromWriteAheadLog()
        {
            var result = WriteAheadLog.ReadLogEntries(false, false);
            if (!result.Success)
            {
                WriteAheadLogProvider.RemoveWAL(SegmentId, Category);
                using var disposeWal = WriteAheadLog;
                throw new WriteAheadLogCorruptionException(SegmentId, result.Exceptions);
            }

            (var newKeys, var newValues) = WriteAheadLogUtility
                .StableSortAndCleanUpDeletedKeys(
                result.Keys,
                result.Values,
                Comparer,
                IsValueDeleted);
            var len = newKeys.Count;
            for (var i = 0; i < len; ++i)
            {
                Dictionary.Add(newKeys[i], newValues[i]);
            }
        }

        public bool TryGetValue(in TKey key, out TValue value)
        {
            return Dictionary.TryGetValue(key, out value);
        }

        public bool Upsert(in TKey key, in TValue value)
        {
            if (Dictionary.ContainsKey(key))
            {
                Dictionary[key] = value;
                WriteAheadLog.Append(key, value);
                return false;
            }
            Dictionary.Add(key, value);
            WriteAheadLog.Append(key, value);
            return true;
        }

        public bool TryDelete(in TKey key)
        {
            Dictionary.TryGetValue(key, out var value);
            MarkValueDeleted(ref value);
            var result = Dictionary.Remove(key);
            WriteAheadLog.Append(key, value);
            return result;
        }

        public void Drop()
        {
            WriteAheadLogProvider.RemoveWAL(SegmentId);
            WriteAheadLog?.Drop();
        }

        public void Dispose()
        {
            WriteAheadLogProvider.RemoveWAL(SegmentId);
            WriteAheadLog?.Dispose();
        }

        public void CompactWriteAheadLog()
        {
            var keys = Dictionary.Keys.ToArray();
            var values = Dictionary.Values.ToArray();
            WriteAheadLog.ReplaceWriteAheadLog(keys, values);

            // recreate the dictionary to avoid empty space in the hash table.
            var newDictionary = new Dictionary<TKey, TValue>();
            var len = keys.Length;
            for (var i = 0; i < len; ++i)
            {
                newDictionary.Add(keys[i], values[i]);
            }
            Dictionary = newDictionary;
        }
    }
}