// The frame's cost measured on a fixed scene (docs/DEBUG.md "Performance"): --perf <frames> runs that many frames
// with a fixed number of simulation steps each (--perf-steps n, default 2: x1.5 at ~30 fps) and a fixed
// interpolation, after --perf-wait <frames> (default 60) to settle; then a PERF report - the sections of the frame
// (GameSession._Process), their finer parts (PerfProbe: entities, view, ...), the simulation's phases (StepProfile),
// the memory allocated per frame, the engine's monitors (draw calls, objects, memory) - and the game quits. With a
// saved game (--load) the scene is the same on every run: the steps are the save's and the frames take the same steps.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Godot;
using PwRemake.Sim.World;
using PwRemake.View;
using PwRemake.Sim.Core;

namespace PwRemake.App;

public sealed partial class GameSession
{
    int _perfFrames, _perfWait = 60, _perfSteps, _perfN = -1;
    double _perfPan, _perfX0;
    readonly Dictionary<string, double> _perfSum = new();
    readonly Dictionary<string, long> _lastBytes = new(), _perfBytesSum = new();
    long _perfBytes, _perfStart;
    int _perfGc0, _perfGc1, _perfGc2;
    TimeSpan _perfPause;
    double _perfMaxFrame;
    readonly List<double> _perfFrameMs = new();

    void PerfConfigure()
    {
        if (!_args.TryGetValue("perf", out var pf) || !int.TryParse(pf, out _perfFrames) || _perfFrames <= 0) return;
        _perfSteps = _args.TryGetValue("perf-steps", out var ps) && int.TryParse(ps, out var n) ? Math.Max(0, n) : 2;
        if (_args.TryGetValue("perf-wait", out var pw) && int.TryParse(pw, out var w)) _perfWait = Math.Max(0, w);
        // --perf-pan <m>: the camera moves that far each frame (to and fro over 200 frames): the work a moving view does
        if (_args.TryGetValue("perf-pan", out var pp)) _perfPan = Json.ParseNum(pp, 0);
        // the camera over the busiest place (the most units and buildings within a 64 m square), unless --cam says
        if (!_args.ContainsKey("cam"))
        {
            var cells = new Dictionary<(int, int), int>();
            foreach (var e in World.Units.Cast<Entity>().Concat(World.Buildings))
                if (e.Alive && !e.Removed) { var k = ((int)Math.Floor(e.Pos.X / 64), (int)Math.Floor(e.Pos.Z / 64)); cells[k] = cells.GetValueOrDefault(k) + 1; }
            if (cells.Count > 0)
            {
                var best = cells.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2).First();
                Camera.X = best.Key.Item1 * 64 + 32; Camera.Z = best.Key.Item2 * 64 + 32;
            }
        }
        _perfX0 = Camera.X;
        if (_camInput != null) _camInput.EdgeScroll = false;       // (the mouse of the test display at an edge)
        L.Info($"PERF: {_perfFrames} frames of {_perfSteps} steps after {_perfWait} frames, camera at {Camera.X:0}, {Camera.Z:0} moving {_perfPan} m a frame");
    }

    bool PerfOn => _perfFrames > 0;

    /// <summary>the keys of a template's clips (--perf-mem)</summary>
    static long PerfKeys(PwRemake.View.Models.ModelTemplate? t)
    {
        long k = 0;
        if (t?.Clips == null) return 0;
        foreach (var n in t.Clips.GetAnimationList())
        {
            var a = t.Clips.GetAnimation(n);
            for (int i = 0; i < a.GetTrackCount(); i++) k += a.TrackGetKeyCount(i);
        }
        return k;
    }

    /// <summary>the fixed steps of a measured frame (instead of the frame's real time)</summary>
    void PerfSteps()
    {
        for (int i = 0; i < _perfSteps; i++)
        {
            Entities.BeforeStep();
            try { Sim.Step(); }
            catch (Exception e) { L.Error($"simulation step {World.StepNo} failed", e); }
            Presenter.Tick(StepSeconds);
            _stepsLastFrame++;
        }
        _acc = StepSeconds * 0.5;
        if (_perfPan != 0) { int k = Math.Max(0, _perfN) % 400; Camera.X = _perfX0 + _perfPan * (k < 200 ? k : 400 - k); }
    }

    /// <summary>end of a frame: the sections of this frame (ms) counted while measuring</summary>
    void PerfFrame(double frameMs)
    {
        if (!PerfOn) return;
        _perfN++;
        if (_perfN == _perfWait)
        {
            World.Profile = new StepProfile();
            PerfProbe.P = new StepProfile();
            _perfBytes = GC.GetAllocatedBytesForCurrentThread();
            _perfPause = GC.GetTotalPauseDuration();
            _perfGc0 = GC.CollectionCount(0); _perfGc1 = GC.CollectionCount(1); _perfGc2 = GC.CollectionCount(2);
            _perfStart = Stopwatch.GetTimestamp();
            return;
        }
        if (_perfN < _perfWait) return;
        foreach (var (k, v) in _last) _perfSum[k] = _perfSum.GetValueOrDefault(k) + v;
        foreach (var (k, v) in _lastBytes) _perfBytesSum[k] = _perfBytesSum.GetValueOrDefault(k) + v;
        _perfSum["frame"] = _perfSum.GetValueOrDefault("frame") + frameMs;
        _perfFrameMs.Add(frameMs);
        _perfMaxFrame = Math.Max(_perfMaxFrame, frameMs);
        if (_perfN - _perfWait < _perfFrames) return;
        // ---- the report
        int n = _perfFrames;
        double wall = (Stopwatch.GetTimestamp() - _perfStart) * 1000.0 / Stopwatch.Frequency;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - _perfBytes;
        var sorted = _perfFrameMs.OrderBy(x => x).ToList();
        var sb = new StringBuilder();
        sb.Append($"PERF {n} frames at game {World.Time:0.0} s, {World.Units.Count} units, {World.Buildings.Count} buildings, drawn {Entities.Drawn}\n");
        sb.Append($"  sections (ms per frame): {string.Join(" · ", _perfSum.Select(kv => $"{kv.Key} {kv.Value / n:0.00}"))}\n");
        sb.Append($"  allocated (KB per frame): {string.Join(" · ", _perfBytesSum.Select(kv => $"{kv.Key} {kv.Value / 1024.0 / n:0.0}"))}; the rest of the frame (other nodes, the engine) {(bytes - _perfBytesSum.Values.Sum()) / 1024.0 / n:0.0}\n");
        sb.Append($"  frame median {sorted[sorted.Count / 2]:0.00} ms, max {_perfMaxFrame:0.0} ms; wall (with rendering) {wall / n:0.0} ms per frame\n");
        sb.Append($"  managed allocations {bytes / 1024.0 / n:0.0} KB per frame; GC {GC.CollectionCount(0) - _perfGc0} / {GC.CollectionCount(1) - _perfGc1} / {GC.CollectionCount(2) - _perfGc2} (gen 0/1/2), pauses {(GC.GetTotalPauseDuration() - _perfPause).TotalMilliseconds:0} ms, heap {GC.GetTotalMemory(false) / 1048576.0:0} MB\n");
        sb.Append($"  engine: {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} draws, {Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)} objects, " +
                  $"{Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1000:0} k primitives, nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)}, objects {Performance.GetMonitor(Performance.Monitor.ObjectCount)}, resources {Performance.GetMonitor(Performance.Monitor.ObjectResourceCount)}\n");
        sb.Append($"  memory: static {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576:0} MB, video {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:0} MB (textures {Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576:0}, buffers {Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1048576:0}), " +
                  $"process {OS.GetStaticMemoryUsage() / 1048576:0} MB static; {Models.Loaded.Count()} model templates\n");
        if (PerfProbe.P is { } vp)
        {
            sb.Append("  view parts (ms per frame):\n");
            foreach (var (p, ms, cnt, by) in vp.Phases) sb.Append($"    {p,-28} {ms / n,8:0.000} ms  {by / 1024.0 / n,8:0.0} KB  n={cnt / (double)n:0.#}\n");
        }
        if (World.Profile is { } sp) sb.Append("  simulation: " + sp.Report(25));
        L.Info(sb.ToString());
        World.Profile = null; PerfProbe.P = null;
        _perfFrames = 0;
        if (!_args.ContainsKey("perf-stay")) GetTree().Quit();
    }
}
