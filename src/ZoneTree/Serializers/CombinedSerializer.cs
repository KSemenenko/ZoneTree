﻿namespace Tenray.ZoneTree.Serializers;

public sealed class CombinedSerializer<TValue1, TValue2> : ISerializer<CombinedValue<TValue1, TValue2>>
{
    readonly ISerializer<TValue1> Serializer1;

    readonly ISerializer<TValue2> Serializer2;

    public CombinedSerializer(
        ISerializer<TValue1> serializer1,
        ISerializer<TValue2> serializer2)
    {
        Serializer1 = serializer1;
        Serializer2 = serializer2;
    }

    public CombinedValue<TValue1, TValue2> Deserialize(Memory<byte> bytes)
    {
        var len = bytes.Length;
        var len1 = BitConverter.ToInt32(bytes.Slice(len - sizeof(int)).Span);
        var len2 = len - len1 - sizeof(int);

        var value1 = Serializer1.Deserialize(bytes.Slice(0, len1));
        var value2 = Serializer2.Deserialize(bytes.Slice(len1, len2));
        return new CombinedValue<TValue1, TValue2>(value1, value2);
    }

    public Memory<byte> Serialize(in CombinedValue<TValue1, TValue2> entry)
    {
        var bytes1 = Serializer1.Serialize(entry.Value1);
        var bytes2 = Serializer2.Serialize(entry.Value2);
        var len1 = bytes1.Length;
        var len2 = bytes2.Length;
        var bytes = new byte[len1 + len2 + sizeof(int)];
        bytes1.CopyTo(bytes);
        bytes2.CopyTo(new Memory<byte>(bytes).Slice(len1));
        Array.Copy(BitConverter.GetBytes(len1), 0, bytes, len1 + len2, sizeof(int));
        return bytes;
    }
}
