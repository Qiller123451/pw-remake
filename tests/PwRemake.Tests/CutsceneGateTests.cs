// Gates in cutscenes and at the start of every mission (player report, MIRAGE mission 16 "Showdown": "in the cutscene
// the gates keep opening and closing rapidly").
//
// The cause was the view: while a cutscene plays the world stands still, and the bound buildings of the scene (mission
// 16's end boss gates, opening at the start of sc_7050) had no clip of the scene's own - the view drew the world's
// standing clip, advanced it by the frame's real time (an actor is "live") and put it back on the world's time
// whenever the two were 0.25 s apart: the gates jumped back four times a second. Now every bound object with clips has
// one of the scene's (Sim/Campaign/SceneClips: a copy of the world's clip that runs on; the ItemAnims' frame ranges),
// and the world's clip goes on from the scene's at its end (SeqActors.Finish).
//
// The test plays every mission of both configurations as the game does - the world steps, a cutscene stops it and is
// played frame by frame (30 fps) through SceneClips with the view's rule for following a clip (EntityView.SyncAnim:
// a new start, or 0.25 s off -> put back), then ends with the SceneEndCommand - and counts how often every gate is seen
// to change: in the world (open / closed, a clip started) and in the scenes (a clip started or put back); the other
// cutscenes with bound buildings are played on the world as it is after the opening.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

/// <summary>A mission UI that plays the cutscenes (the test plays their timelines, then ends them).</summary>
public sealed class ScenePresenter : NullPresenter
{
    public SequencePlay? Pending;
    public readonly List<string> Played = new();
    public override bool IsBusy => Pending != null;
    public override bool PlaySequence(SequencePlay play)
    {
        if (play.Data?["play"] is not JsonObject p || p["end"].Num() <= 0 || play.Actors == null) return false;
        Pending = play; Played.Add(play.Request.Path);
        return true;
    }
}

[TestClass]
public static class CutsceneGateTests
{
    const double FRAME = 1 / 30.0;

    /// <summary>The view's model of an entity's clip (EntityView.SyncAnim): the clip it shows and how far it is.</summary>
    sealed class ViewClip { public int Serial = int.MinValue; public string? Clip; public double T; public int Jumps; public List<string> Log = new(); }

    static void Follow(ViewClip v, AnimCtl? a, double dt, double sceneT)
    {
        var cur = a?.Cur;
        if (a == null || cur == null) return;
        if (a.Serial != v.Serial)
        {
            if (v.Serial != int.MinValue) { v.Jumps++; v.Log.Add($"{sceneT:0.00} start {cur.Clip} at {cur.Time:0.00}"); }
            v.Serial = a.Serial; v.Clip = cur.Clip; v.T = cur.Time;
            return;
        }
        if (!cur.IsRunning) { if (Math.Abs(v.T - cur.Time) > 0.25) { v.Jumps++; v.Log.Add($"{sceneT:0.00} held {cur.Clip} {v.T:0.00} -> {cur.Time:0.00}"); } v.T = cur.Time; return; }
        v.T += dt * cur.TimeScale;
        if (cur.Loop && cur.Duration > 0) v.T %= cur.Duration; else v.T = Math.Min(v.T, cur.Duration);
        double d = Math.Abs(v.T - cur.Time);
        if (cur.Loop && cur.Duration > 0) d = Math.Min(d, cur.Duration - d);
        if (d > 0.25) { v.Jumps++; v.Log.Add($"{sceneT:0.00} put back {cur.Clip} {v.T:0.00} -> {cur.Time:0.00}"); v.T = cur.Time; }
    }

    /// <summary>
    /// One cutscene as the game plays it: the world stands still; every frame the stage poses the bound actors and runs
    /// their clips (SceneClips); the view follows the clip of the scene (or, for an object without one, the world's,
    /// on the frame's real time). Returns per bound building: (name, items, view jumps, log).
    /// </summary>
    public static List<(string Name, int Items, int Jumps, string Log)> PlayScene(SeqActors actors)
    {
        var P = actors.Play;
        double t0 = P["start"].Num(), t1 = P["end"].Num();
        var clips = new SceneClips(actors);
        var bound = actors.List.Where(A => A.E is Building { Alive: true } && A.E.Anim != null).ToList();
        var world = bound.ToDictionary(A => A, A => (A.E!.Anim!.Serial, A.E.Anim.Cur?.Time));
        var views = bound.ToDictionary(A => A, _ => new ViewClip());
        for (double t = t0; t < t1 + FRAME; t += FRAME)
        {
            foreach (var A in actors.List) if (A.E != null) clips.Apply(A, actors.PoseAt(A, t), t, t > t0 ? FRAME : 0);
            foreach (var A in bound) Follow(views[A], clips.Of(A.E!) ?? A.E!.Anim, FRAME, t);
        }
        foreach (var A in bound)
            if (A.E!.Anim!.Serial != world[A].Serial || A.E.Anim.Cur?.Time != world[A].Item2) throw new Exception($"the scene changed the world's clip of {A.A["name"].Str()}");
        return bound.Select(A => (A.A["name"].Str(), (A.A["items"]?.AsArray() ?? new JsonArray()).Count(it => it?["k"].Str() is "anim" or "act"), views[A].Jumps, string.Join("; ", views[A].Log.Take(6)))).ToList();
    }

    /// <summary>a gate's changes in the world: its open flag and its clip's starts, by game time</summary>
    sealed class GateWatch { public bool Open; public int Serial; public readonly List<double> Changes = new(); }

    static List<string> RunMission(TestContext t, string cfg, int id, double seconds, Action<MissionSession>? setup = null, Action<MissionSession, ScenePresenter>? extra = null)
    {
        var P = new ScenePresenter();
        var s = CampaignTests.Session(t, cfg, id, presenter: P);
        var W = s.World; var bad = new List<string>();
        setup?.Invoke(s);
        var gates = new Dictionary<Building, GateWatch>();
        void Watch()
        {
            foreach (var b in W.Buildings)
            {
                if (b.Def?.Gate != true || !b.Alive) continue;
                int serial = b.Anim?.Serial ?? 0;
                if (!gates.TryGetValue(b, out var g)) { gates[b] = new GateWatch { Open = b.GateOpen, Serial = serial }; continue; }
                if (g.Open != b.GateOpen || g.Serial != serial) { g.Changes.Add(W.Time); g.Open = b.GateOpen; g.Serial = serial; }
            }
        }
        void Scene()
        {
            var play = P.Pending!;
            foreach (var (name, items, jumps, log) in PlayScene(play.Actors!))
                if (jumps > items + 1) bad.Add($"{cfg} {id} {play.Request.Path}: {name} seen to change {jumps} times ({items} animation items): {log}");
            P.Pending = null;
            W.Enqueue(new SceneEndCommand { Kind = "sequence", Timeline = true });
        }
        int steps = (int)Math.Round(seconds / GameWorld.STEP);
        for (int i = 0; i < steps && !s.Over; i++)
        {
            if (P.Pending != null) Scene();
            s.Step();
            Watch();
        }
        if (P.Pending != null) { Scene(); s.Step(); }
        extra?.Invoke(s, P);
        // in the world: no gate changes more than 3 times within 10 s (an automatic gate closes 8 s after the last unit)
        foreach (var (b, g) in gates)
            for (int i = 0; i + 3 < g.Changes.Count; i++)
                if (g.Changes[i + 3] - g.Changes[i] < 10) { bad.Add($"{cfg} {id}: gate {b.Name} at ({b.Pos.X:0}, {b.Pos.Z:0}) changed {g.Changes.Count} times, 4 within {g.Changes[i + 3] - g.Changes[i]:0.0} s from {g.Changes[i]:0.0} s"); break; }
        t.Log($"{cfg} {id}: {gates.Count} gates, {P.Played.Count} cutscenes played ({string.Join(", ", P.Played.Select(TrigUtil.FilePart))}), {gates.Values.Sum(g => g.Changes.Count)} gate changes");
        return bad;
    }

    /// <summary>Every mission of both configurations: the opening (60 game seconds, its cutscenes played as the game
    /// plays them), then every other cutscene with a bound building (gates, the Scorpion, garages, cages ...) bound on
    /// the world as it is - no gate flickers in the world, no bound building in a scene (the view sees a change only
    /// when an animation item asks for one). PW_CAMPAIGN_MISSIONS=16,13: only those.</summary>
    public static void GatesSteadyEveryMission(TestContext t)
    {
        var only = (Environment.GetEnvironmentVariable("PW_CAMPAIGN_MISSIONS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        var bad = new List<string>(); int n = 0, scenes = 0, objs = 0;
        foreach (var cfg in new[] { "base", "mirage" })
        {
            var c = CampaignTests.LoadCfgPublic(cfg);
            if (c == null) { t.Log($"configuration {cfg} not found"); continue; }
            foreach (var e in MissionEntry.LoadIndex(c.Config.MissionsDir))
            {
                if (only.Count > 0 && !only.Contains(e.Id)) continue;
                if (WorldBuilder.FindMap(c.Config, e.Map) == null) continue;
                try
                {
                    bad.AddRange(RunMission(t, cfg, e.Id, 60, null, (s, P) =>
                    {
                        foreach (var (path, sq) in s.Campaign.Data.Sequences)
                        {
                            if (P.Played.Contains(path) || sq?["play"] is not JsonObject pj || pj["end"].Num() <= 0) continue;
                            var actors = new SeqActors(s.Campaign, pj);
                            if (!actors.List.Any(A => A.E is Building { Alive: true, Anim: not null })) continue;
                            scenes++;
                            foreach (var (name, items, jumps, log) in PlayScene(actors))
                            {
                                objs++;
                                if (jumps > items + 1) bad.Add($"{cfg} {e.Id} {TrigUtil.FilePart(path)}: {name} seen to change {jumps} times ({items} animation items): {log}");
                            }
                        }
                    }));
                    n++;
                }
                catch (SkipException) { }
            }
        }
        foreach (var b in bad) t.Log("  " + b);
        t.Log($"{n} missions; besides their openings {scenes} cutscenes with {objs} bound buildings");
        if (n == 0) t.Skip("no missions");
        t.True(bad.Count == 0, $"{bad.Count} gates / buildings flicker");
    }

    /// <summary>Mission 16 (both configurations): the end boss gates open during sc_7050 and are open after it (not
    /// opened again); in sc_7052 gate 0 holds open until 37.5 s, then closes (frames 0..50 of "close"), and stays
    /// closed after the scene, as the trigger "01_close_gates" leaves it.</summary>
    public static void Mission16Gates(TestContext t)
    {
        foreach (var cfg in new[] { "mirage", "base" })
        {
            // the gates' clips as the user's data has them ("open" / "close", frames 0..50: the SEAS gate's rig x 1.25) -
            // the container's test data has no model of seas_endboss_gate; the doors open at the start (GateState 0)
            var bad = RunMission(t, cfg, 16, 5, s =>
            {
                foreach (var b in s.World.Buildings.Where(b => b.Name == "seas_endboss_gate"))
                {
                    if (b.Anim?.Has("open") != true) { var cs = new ClipSet(); cs.Add("open", 2.0); cs.Add("close", 2.0); b.Anim = new AnimCtl(cs, b); }
                    s.World.SetGate(b, b.GateState);
                }
            }, (s, P) =>
            {
                t.True(P.Played.Any(p => p.EndsWith("sc_7050.seq")), $"{cfg}: sc_7050 played at the start");
                var g0 = (Building)s.Campaign.Objects.ByName("seas_endboss_gate_0")!.Entity!;
                t.True(g0.GateOpen && g0.Anim?.CurName == "open" && g0.Anim.Cur!.Time >= g0.Anim.Cur.Duration - 1e-6, $"{cfg}: gate 0 open after sc_7050, its clip at the end ({g0.Anim})");
                // sc_7052 (after the attack forces): played on the world as it is; the frames of gate 0 in the scene
                var data = s.Campaign.Data.Sequences.First(kv => kv.Key.EndsWith("sc_7052.seq")).Value!["play"]!;
                var actors = new SeqActors(s.Campaign, data);
                var A = actors.List.First(a => a.A["name"].Str() == "seas_endboss_gate_0");
                var clips = new SceneClips(actors);
                double Pose(double at) { clips.Apply(A, actors.PoseAt(A, at), at, FRAME); var c = clips.Of(A.E!)!; t.True(c.CurName == "close", $"{cfg}: close at {at}"); return c.Cur!.Time; }
                t.Near(0, Pose(10), 1e-9, $"{cfg}: sc_7052 gate 0 held open (close, frame 0) at 10 s");
                t.Near(0, Pose(37), 1e-9, $"{cfg}: ... at 37 s");
                t.Near(1.0, Pose(38.48), 1e-6, $"{cfg}: closing at 38.5 s (frame 25)");
                t.True(clips.Of(A.E!)!.Cur!.Paused, $"{cfg}: the scene sets the frame (held clip)");
                actors.Moved = true;
                actors.Finish(true);
                t.True(g0.Anim!.CurName == "close" && g0.Anim.Cur!.Time >= 1.99, $"{cfg}: after sc_7052 the world's gate 0 stays closed ({g0.Anim})");
            });
            foreach (var b in bad) t.Log("  " + b);
            t.True(bad.Count == 0, $"{cfg} 16: gates flicker");
        }
    }

    /// <summary>The frame ranges of ItemAnim (SEQUENCE_FORMAT.md §8.1): 25 frames per second, inclusive, repeated, held.</summary>
    public static void FrameRanges(TestContext t)
    {
        JsonNode It(double at, double f0, double f1, int rep) => new JsonObject { ["k"] = "anim", ["t"] = at, ["len"] = 10, ["anim"] = "x", ["rep"] = (double)rep, ["f0"] = f0, ["f1"] = f1 };
        t.Near(0, SeqActors.FrameTime(It(5, 0, 0, 1), 4, 20)!.Value, 1e-9, "a single frame is held");
        t.Near(2, SeqActors.FrameTime(It(5, 50, 50, 1), 4, 6)!.Value, 1e-9, "frame 50 = 2 s");
        t.Near(1, SeqActors.FrameTime(It(5, 0, 50, 1), 4, 6)!.Value, 1e-9, "frames 0..50: 1 s in");
        t.Near(2, SeqActors.FrameTime(It(5, 0, 50, 1), 4, 9)!.Value, 1e-9, "then held on the last frame");
        t.Near(0.5, SeqActors.FrameTime(It(0, 0, 25, 3), 4, 2.5)!.Value, 1e-9, "repeat 3: the range again");
        t.Near(1, SeqActors.FrameTime(It(0, 0, 25, 3), 4, 3.5)!.Value, 1e-9, "after 3 times held");
        t.Near(4, SeqActors.FrameTime(It(0, 0, 4294967294, 1), 4, 9)!.Value, 1e-9, "a range past the clip ends at its end");
        t.True(SeqActors.FrameTime(new JsonObject { ["k"] = "act", ["t"] = 0 }, 4, 1) == null, "an ItemAnimAct is no frame range");
    }
}
