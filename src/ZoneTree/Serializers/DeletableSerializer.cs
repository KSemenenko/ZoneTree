﻿using Tenray.ZoneTree.PresetTypes;

namespace Tenray.ZoneTree.Serializers;

public sealed class DeletableSerializer<TValue> : ISerializer<Deletable<TValue>>
    where TValue : unmanaged
{
    public Deletable<TValue> Deserialize(Memory<byte> bytes)
    {
        return BinarySerializerHelper.FromByteArray<Deletable<TValue>>(bytes);
    }

    public Memory<byte> Serialize(in Deletable<TValue> entry)
    {
        return BinarySerializerHelper.ToByteArray(entry);
    }
}
