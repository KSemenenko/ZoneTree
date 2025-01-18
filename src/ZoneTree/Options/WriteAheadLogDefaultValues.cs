﻿namespace Tenray.ZoneTree.Options;

public static class WriteAheadLogDefaultValues
{
    public static readonly WriteAheadLogMode WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed;

    public static readonly int CompressionBlockSize = 1024 * 32 * 8;

    public static readonly CompressionMethod CompressionMethod = CompressionMethod.LZ4;

    public static readonly int CompressionLevel = CompressionLevels.LZ4Fastest;

    public static readonly bool SyncCompressedModeEnableTailWriterJob = true;

    public static readonly int SyncCompressedModeTailWriterJobInterval = 500;

    public static readonly int AsyncCompressedModeEmptyQueuePollInterval = 100;
}
