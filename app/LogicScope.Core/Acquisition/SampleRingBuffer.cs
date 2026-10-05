using LogicScope.Core.Models;

namespace LogicScope.Core.Acquisition;

public readonly record struct SampleEnvelope(
    ushort First,
    ushort Last,
    ushort AnyHigh,
    ushort AnyLow,
    bool HasSamples);

/// <summary>
/// Fixed-capacity, thread-safe sample storage. It never grows with capture
/// duration: old samples are overwritten after Capacity samples. A compact
/// 256-sample summary pyramid lets the waveform aggregate large visible spans
/// without materializing them at UI resolution.
/// </summary>
public sealed class SampleRingBuffer
{
    public const int DefaultCapacityBytes = 16 * 1024 * 1024;
    private const int PageSamples = 256;

    private readonly ushort[] _samples;
    private readonly PageSummary[] _pages;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private long _nextIndex;
    private long _oldestIndex;

    public SampleRingBuffer(int capacitySamples = DefaultCapacityBytes / sizeof(ushort))
    {
        if (capacitySamples < PageSamples || capacitySamples % PageSamples != 0)
            throw new ArgumentOutOfRangeException(nameof(capacitySamples),
                "Capacity must be at least 256 samples and a multiple of 256.");
        _samples = new ushort[capacitySamples];
        _pages = Enumerable.Range(0, capacitySamples / PageSamples)
            .Select(_ => new PageSummary()).ToArray();
    }

    public int Capacity => _samples.Length;
    public long OldestIndex
    {
        get { _lock.EnterReadLock(); try { return _oldestIndex; } finally { _lock.ExitReadLock(); } }
    }
    public long NextIndex
    {
        get { _lock.EnterReadLock(); try { return _nextIndex; } finally { _lock.ExitReadLock(); } }
    }
    public long AvailableSamples
    {
        get { _lock.EnterReadLock(); try { return _nextIndex - _oldestIndex; } finally { _lock.ExitReadLock(); } }
    }

    public void Reset()
    {
        _lock.EnterWriteLock();
        try
        {
            _nextIndex = 0;
            _oldestIndex = 0;
            foreach (var page in _pages) page.Reset(-1);
        }
        finally { _lock.ExitWriteLock(); }
    }

    public long Append(ReadOnlySpan<ushort> samples)
    {
        _lock.EnterWriteLock();
        try
        {
            var firstIndex = _nextIndex;
            foreach (var sample in samples)
            {
                var index = _nextIndex++;
                _samples[(int)(index % _samples.Length)] = sample;
                UpdateSummary(index, sample);
            }
            _oldestIndex = Math.Max(0, _nextIndex - _samples.Length);
            return firstIndex;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool TryGetSample(long absoluteIndex, out ushort sample)
    {
        _lock.EnterReadLock();
        try
        {
            if (absoluteIndex < _oldestIndex || absoluteIndex >= _nextIndex)
            {
                sample = 0;
                return false;
            }
            sample = _samples[(int)(absoluteIndex % _samples.Length)];
            return true;
        }
        finally { _lock.ExitReadLock(); }
    }

    public long? FindPattern(TriggerPattern pattern, long startInclusive, long endExclusive)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        _lock.EnterReadLock();
        try
        {
            var start = Math.Max(startInclusive, _oldestIndex);
            var end = Math.Min(endExclusive, _nextIndex);
            for (var index = start; index < end; index++)
                if (pattern.Matches(_samples[(int)(index % _samples.Length)])) return index;
            return null;
        }
        finally { _lock.ExitReadLock(); }
    }

    public long? FindEdge(int channel, TriggerEdge edge, long startInclusive, long endExclusive)
    {
        if ((uint)channel >= 16) throw new ArgumentOutOfRangeException(nameof(channel));
        if (edge is not TriggerEdge.Rising and not TriggerEdge.Falling)
            throw new ArgumentOutOfRangeException(nameof(edge));
        _lock.EnterReadLock();
        try
        {
            var start = Math.Max(startInclusive, _oldestIndex + 1);
            var end = Math.Min(endExclusive, _nextIndex);
            var mask = (ushort)(1u << channel);
            for (var index = start; index < end; index++)
            {
                var previous = (_samples[(int)((index - 1) % _samples.Length)] & mask) != 0;
                var current = (_samples[(int)(index % _samples.Length)] & mask) != 0;
                if (edge == TriggerEdge.Rising ? !previous && current : previous && !current)
                    return index;
            }
            return null;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Copies no more than the requested visible range into a snapshot.</summary>
    public SampleWindow ReadWindow(long startIndex, long requestedSamples, uint sampleRateHz)
    {
        if (requestedSamples < 0) throw new ArgumentOutOfRangeException(nameof(requestedSamples));
        _lock.EnterReadLock();
        try
        {
            var start = Math.Max(startIndex, _oldestIndex);
            var end = Math.Min(_nextIndex, SaturatingAdd(startIndex, requestedSamples));
            if (end < start) end = start;
            var length = checked((int)Math.Min(int.MaxValue, end - start));
            var result = new ushort[length];
            for (var i = 0; i < length; i++)
                result[i] = _samples[(int)((start + i) % _samples.Length)];
            return new SampleWindow(start, sampleRateHz, result);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Returns one envelope per screen column for a visible sample-index range.
    /// Memory use is O(pixelWidth), even if the visible range contains millions
    /// of samples. Page summaries are combined where possible; only partial
    /// pages are inspected sample-by-sample.
    /// </summary>
    public SampleEnvelope[] AggregateVisible(long startIndex, long visibleSamples, int pixelWidth)
    {
        if (visibleSamples < 0) throw new ArgumentOutOfRangeException(nameof(visibleSamples));
        if (pixelWidth is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        var result = new SampleEnvelope[pixelWidth];
        if (visibleSamples == 0) return result;

        _lock.EnterReadLock();
        try
        {
            var requestedEnd = SaturatingAdd(startIndex, visibleSamples);
            for (var x = 0; x < pixelWidth; x++)
            {
                var columnStart = startIndex + (long)(((Int128)visibleSamples * x) / pixelWidth);
                var columnEnd = startIndex + (long)(((Int128)visibleSamples * (x + 1)) / pixelWidth);
                if (columnEnd <= columnStart) columnEnd = Math.Min(requestedEnd, columnStart + 1);
                var cursor = Math.Max(columnStart, _oldestIndex);
                var end = Math.Min(columnEnd, _nextIndex);
                if (cursor >= end) continue;

                ushort first = 0;
                ushort last = 0;
                ushort anyHigh = 0;
                ushort allHigh = 0xFFFF;
                bool hasFirst = false;

                while (cursor < end)
                {
                    var pageNumber = cursor / PageSamples;
                    var pageOffset = cursor % PageSamples;
                    var page = _pages[(int)(pageNumber % _pages.Length)];
                    var canUseWholePage = pageOffset == 0 && end - cursor >= PageSamples &&
                                          page.Tag == pageNumber && page.Count == PageSamples;
                    if (canUseWholePage)
                    {
                        if (!hasFirst)
                        {
                            first = page.First;
                            hasFirst = true;
                        }
                        last = page.Last;
                        anyHigh |= page.Or;
                        allHigh &= page.And;
                        cursor += PageSamples;
                        continue;
                    }

                    var value = _samples[(int)(cursor % _samples.Length)];
                    if (!hasFirst)
                    {
                        first = value;
                        hasFirst = true;
                    }
                    last = value;
                    anyHigh |= value;
                    allHigh &= value;
                    cursor++;
                }

                result[x] = new SampleEnvelope(first, last, anyHigh,
                    (ushort)~allHigh, hasFirst);
            }
        }
        finally { _lock.ExitReadLock(); }
        return result;
    }

    private void UpdateSummary(long absoluteIndex, ushort sample)
    {
        var pageNumber = absoluteIndex / PageSamples;
        var page = _pages[(int)(pageNumber % _pages.Length)];
        if (page.Tag != pageNumber) page.Reset(pageNumber);
        if (page.Count == 0)
        {
            page.First = sample;
            page.And = sample;
            page.Or = sample;
        }
        else
        {
            page.And &= sample;
            page.Or |= sample;
        }
        page.Last = sample;
        page.Count++;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right) return long.MaxValue;
        if (right < 0 && left < long.MinValue - right) return long.MinValue;
        return left + right;
    }

    private sealed class PageSummary
    {
        public long Tag = -1;
        public int Count;
        public ushort First;
        public ushort Last;
        public ushort And;
        public ushort Or;

        public void Reset(long tag)
        {
            Tag = tag;
            Count = 0;
            First = Last = Or = 0;
            And = 0xFFFF;
        }
    }
}
