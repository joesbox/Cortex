using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Cortex.ViewModels.IoFlow;

public sealed class IoFlowLiveHistory
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);
    private readonly List<IoFlowLiveSample> _samples = [];

    public IReadOnlyList<IoFlowLiveSample> Samples => _samples;

    public static TimeSpan Now => Stopwatch.GetElapsedTime(0);

    public void Record(TimeSpan timestamp, double? value)
    {
        Prune(timestamp);
        value = value is double number && double.IsFinite(number) ? number : null;
        if (_samples.Count > 0 && timestamp - _samples[^1].Timestamp < SampleInterval)
            return;
        _samples.Add(new IoFlowLiveSample(timestamp, value));
    }

    public void Prune(TimeSpan now)
    {
        int expired = _samples.FindIndex(sample => sample.Timestamp >= now - Window);
        if (expired < 0)
            _samples.Clear();
        else if (expired > 0)
            _samples.RemoveRange(0, expired);
    }

    public void Clear() => _samples.Clear();
}

public readonly record struct IoFlowLiveSample(TimeSpan Timestamp, double? Value);
