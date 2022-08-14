﻿using Tenray.ZoneTree.Core;

namespace Tenray.ZoneTree.Serializers;

public class DateTimeSerializer : ISerializer<DateTime>
{
    public DateTime Deserialize(byte[] bytes)
    {
        return new DateTime(BitConverter.ToInt64(bytes));
    }

    public byte[] Serialize(in DateTime entry)
    {
        return BitConverter.GetBytes(entry.Ticks);
    }
}