// Where the time of World.Step goes (a debug aid; off unless World.Profile is set): the phases of the step (commands,
// timers, effects, the units by task, the buildings, projectiles, separation, the computer players, the campaign) in
// milliseconds over all steps measured. tests/PwRemake.Tests/SoakTests.cs SoakPerf prints it; the game's debug
// overlay can too. Not part of the JS (the browser's profiler did this).
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace PwRemake.Sim.World;

public sealed class StepProfile
{
    /// <summary>the profile of the world being stepped (World.Step sets it while measuring, else null): for parts deep down
    /// that do not know the world - the path searches ("nav:find", "nav:coarse" the coarse graph rebuilt,
    /// "nav:clearance"), the tech trees rebuilt ("tt:rebuild")</summary>
    [System.ThreadStatic] public static StepProfile? Current;

    readonly Dictionary<string, (long Ticks, long N, long Bytes)> _phases = new();
    public long Steps;
    public double MaxStepMs;
    public long MaxStepNo;
    long _mark, _bmark;

    static readonly Dictionary<(string, string), string> s_keys = new();
    /// <summary>prefix + name, made once (the profile itself must not allocate per entity)</summary>
    public static string Key(string prefix, string name)
    {
        if (!s_keys.TryGetValue((prefix, name), out var k)) s_keys[(prefix, name)] = k = prefix + name;
        return k;
    }
    public void Begin() { _mark = Stopwatch.GetTimestamp(); _bmark = System.GC.GetAllocatedBytesForCurrentThread(); _gc0 = System.GC.CollectionCount(0); _gc1 = System.GC.CollectionCount(1); }
    int _gc0, _gc1;
    /// <summary>the slowest step had a garbage collection in it (gen 0 / gen 1 or more); the slowest step without one</summary>
    public string WorstGc = "";
    public double MaxStepNoGcMs; public long MaxStepNoGcNo;
    public List<(string Phase, double Ms)> WorstNoGc = new();
    /// <summary>the time (and the memory allocated) since the last mark goes to this phase</summary>
    public void Mark(string phase)
    {
        long now = Stopwatch.GetTimestamp(), b = System.GC.GetAllocatedBytesForCurrentThread();
        Add(phase, now - _mark, b - _bmark);
        _mark = now; _bmark = b;
    }
    // a second, nested clock (inside a phase: the parts of a building's update)
    long _smark, _sbmark;
    public void Sub() { _smark = Stopwatch.GetTimestamp(); _sbmark = System.GC.GetAllocatedBytesForCurrentThread(); }
    public void SubMark(string phase)
    {
        long now = Stopwatch.GetTimestamp(), b = System.GC.GetAllocatedBytesForCurrentThread();
        Add(phase, now - _smark, b - _sbmark);
        _smark = now; _sbmark = b;
    }
    /// <summary>the slowest single path search (NavGrid.Find) and what it was</summary>
    public double MaxFindMs; public string MaxFind = "";
    public void Find(long ticks, int n, double sx, double sz, double tx, double tz, int need, bool breach, bool found, bool coarse)
    {
        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        if (ms > MaxFindMs) { MaxFindMs = ms; MaxFind = $"{sx:0},{sz:0} -> {tx:0},{tz:0} grid {n} need {need}{(breach ? " breach" : "")}{(coarse ? " coarse" : "")} found {found}"; }
    }
    /// <summary>the slowest single update (a unit's) and what it was</summary>
    public double MaxSingleMs; public string MaxSingle = "";
    public void Single(long ticks, string phase, Unit u)
    {
        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        // (no lambda for the text: its captures were allocated for every unit and step and counted in "units")
        if (ms > MaxSingleMs) { MaxSingleMs = ms; MaxSingle = $"{phase} {u.Name}#{u.Id} at {u.Pos.X:0},{u.Pos.Z:0} path {u.Path.Count} target {u.Task.Target?.Name}"; }
    }
    public void Add(string phase, long ticks, long bytes = 0)
    {
        _phases.TryGetValue(phase, out var v);
        _phases[phase] = (v.Ticks + ticks, v.N + 1, v.Bytes + bytes);
        _step.TryGetValue(phase, out var c);
        _step[phase] = c + ticks;
    }
    readonly Dictionary<string, long> _step = new();
    /// <summary>the phases of the slowest step (ms)</summary>
    public List<(string Phase, double Ms)> Worst = new();
    /// <summary>World.Step: the step is over (ms: its time)</summary>
    public void EndStep(double ms, long stepNo)
    {
        Steps++;
        int g0 = System.GC.CollectionCount(0) - _gc0, g1 = System.GC.CollectionCount(1) - _gc1;
        if (ms > MaxStepMs)
        {
            MaxStepMs = ms; MaxStepNo = stepNo;
            Worst = _step.Select(kv => (kv.Key, kv.Value * 1000.0 / Stopwatch.Frequency)).OrderByDescending(x => x.Item2).ToList();
            WorstGc = g0 == 0 ? "no collection" : g1 > 0 ? "a gen 1+ collection" : "a gen 0 collection";
        }
        if (g0 == 0 && ms > MaxStepNoGcMs)
        {
            MaxStepNoGcMs = ms; MaxStepNoGcNo = stepNo;
            WorstNoGc = _step.Select(kv => (kv.Key, kv.Value * 1000.0 / Stopwatch.Frequency)).OrderByDescending(x => x.Item2).ToList();
        }
        _step.Clear();
    }
    public void Clear() { _phases.Clear(); Steps = 0; MaxStepMs = 0; MaxStepNo = 0; MaxStepNoGcMs = 0; MaxStepNoGcNo = 0; MaxFindMs = 0; MaxSingleMs = 0; }
    public IEnumerable<(string Phase, double Ms, long N, long Bytes)> Phases =>
        _phases.Select(kv => (kv.Key, kv.Value.Ticks * 1000.0 / Stopwatch.Frequency, kv.Value.N, kv.Value.Bytes)).OrderByDescending(x => x.Item2);
    /// <summary>the phases by time: total ms, ms per step, share</summary>
    public string Report(int top = 30)
    {
        double total = _phases.Where(kv => !kv.Key.Contains(':')).Sum(kv => kv.Value.Ticks) * 1000.0 / Stopwatch.Frequency;
        var sb = new StringBuilder();
        sb.Append($"{Steps} steps, {total / System.Math.Max(1, Steps):0.000} ms per step, max {MaxStepMs:0.0} ms (step {MaxStepNo})\n");
        foreach (var (p, ms, n, by) in Phases.Take(top))
            sb.Append($"  {p,-28} {ms,10:0.0} ms  {ms / System.Math.Max(1, Steps),8:0.000} ms/step  {(total > 0 ? ms / total * 100 : 0),5:0.0} %  {by / 1024.0 / System.Math.Max(1, Steps),8:0.0} KB/step  n={n}\n");
        sb.Append($"  slowest step ({WorstGc}): " + string.Join(", ", Worst.Take(8).Select(w => $"{w.Phase} {w.Ms:0.0}")) + "\n");
        sb.Append($"  slowest step without a collection: {MaxStepNoGcMs:0.0} ms (step {MaxStepNoGcNo}): " + string.Join(", ", WorstNoGc.Take(8).Select(w => $"{w.Phase} {w.Ms:0.0}")) + "\n");
        sb.Append($"  slowest single update: {MaxSingleMs:0.0} ms {MaxSingle}\n");
        sb.Append($"  slowest path search: {MaxFindMs:0.0} ms {MaxFind}\n");
        return sb.ToString();
    }
}
