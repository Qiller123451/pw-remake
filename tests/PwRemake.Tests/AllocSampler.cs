// What the simulation allocates, by type (PerfReplaySkirmish with PW_PERF_ALLOC=1): the runtime's allocation ticks (one
// event per ~100 KB allocated, with the type of the object that crossed the mark) counted per type - a sample, so a
// type's share of the ticks is about its share of the bytes. No stacks (the events arrive on another thread); closures
// show as their display classes (<>c__DisplayClass..), delegates as Func / Action.
using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Text;

namespace PwRemake.Tests;

public sealed class AllocSampler : EventListener
{
    readonly Dictionary<string, (long N, long Bytes)> _types = new();
    readonly object _lock = new();
    public bool On;

    protected override void OnEventSourceCreated(EventSource s)
    {
        if (s.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(s, EventLevel.Verbose, (EventKeywords)0x1);     // GC keyword
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!On || e.EventName == null || !e.EventName.StartsWith("GCAllocationTick") || e.Payload == null) return;
        int ti = e.PayloadNames!.IndexOf("TypeName"), ai = e.PayloadNames.IndexOf("AllocationAmount64");
        string type = ti >= 0 ? e.Payload[ti]?.ToString() ?? "?" : "?";
        long bytes = ai >= 0 ? Convert.ToInt64(e.Payload[ai]) : 100_000;
        lock (_lock) { _types.TryGetValue(type, out var v); _types[type] = (v.N + 1, v.Bytes + bytes); }
    }

    public string Report(int top = 30)
    {
        lock (_lock)
        {
            long all = _types.Values.Sum(v => v.Bytes);
            var sb = new StringBuilder($"allocation ticks by type ({all / 1048576.0:0} MB sampled):\n");
            foreach (var (t, v) in _types.OrderByDescending(kv => kv.Value.Bytes).Take(top).Select(kv => (kv.Key, kv.Value)))
                sb.Append($"  {v.Bytes * 100.0 / Math.Max(1, all),5:0.0} %  {v.N,6}  {t}\n");
            return sb.ToString();
        }
    }
}
