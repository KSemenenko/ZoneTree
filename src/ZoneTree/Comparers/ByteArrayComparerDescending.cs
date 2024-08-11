﻿namespace Tenray.ZoneTree.Comparers;

public sealed class ByteArrayComparerDescending : IRefComparer<Memory<byte>>
{
    public int Compare(in Memory<byte> x, in Memory<byte> y)
    {
        var len = Math.Min(x.Length, y.Length);
        var spanX = x.Span;
        var spanY = y.Span;
        for (var i = 0; i < len; ++i)
        {
            var r = spanY[i].CompareTo(spanX[i]);
            if (r < 0)
                return -1;
            if (r > 0)
                return 1;
        }
        return y.Length - x.Length;
    }
}