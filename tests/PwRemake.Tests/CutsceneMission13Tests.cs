// Mission 13's opening cutscene sc_4070 (player report, 2026-11-12) and every scene with the same causes:
//
// * "When a unit comes out of the main ship during the opening cutscene, the hull of the ship doesn't open." The scene
//   has an ItemAnim "unload" (frames 0..100 from 44.6 s) on its carrier seas_carrier_10, which binds (owner -1: an
//   ownerless object of the class, SEQUENCE_FORMAT.md §9.3) the map's seas_carrier_7 - placed as scenery (a Prop). Only
//   entities got the scene's clips (SceneClips): the bound props played none of their animation items.
// * "Bela moving his mouth when Cole was speaking": the talk gestures (talking_*, seq_talk_*) open and close the jaw;
//   the scenes run them past the actor's own line. A talk gesture's jaw now moves only while its actor speaks, and a
//   line the scene put on another actor goes to its character (SceneSpeech).
// * "With faction swap to SEAS, the SEAS Black Widow in the cutscene is constantly playing its attack animation": the
//   player's aje_allosaurus_1 is drawn as the SEAS' seas_wehrspinne, whose turret part seas_wehrspinne_top took the
//   composite table's clip "attack_front" (the clip it shoots with) as its loop. The Black Widows of the base scenes did
//   the same (sc_4070's three, sc_4080's). Parts now rest (SeqActors.RestClip); a clip the swapped model lacks maps to its
//   own of the same kind, never an attack the scene does not ask for (SceneClips.SceneClip).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.Tests;

[TestClass]
public static class CutsceneMission13Tests
{
    static JsonNode Play(MissionSession s, string seq) => s.Campaign.Data.Sequences.First(kv => kv.Key.EndsWith(seq, StringComparison.OrdinalIgnoreCase)).Value!["play"]!.DeepClone();

    /// <summary>sc_4070's carrier: bound to the map's ownerless carrier (a prop), its hull opens over frames 0..100 from
    /// 44.6 s and stays open after the scene; every bound prop of every scene plays its animation items.</summary>
    public static void CarrierHullOpens(TestContext t)
    {
        var s = CampaignTests.Session(t, "base", 13);
        var A = new SeqActors(s.Campaign, Play(s, "sc_4070.seq"));
        var car = A.List.First(a => a.A["name"].Str() == "seas_carrier_10" && a.A["items"]!.AsArray().Any(i => i!["anim"].Str() == "unload"));
        if (car.Prop == null) t.Skip("the carrier is not bound to a prop in this content");
        t.True(car.Prop.Anim?.Has("unload") == true, "the carrier's model has its unload clip");
        var clips = new SceneClips(A);
        var an = clips.Of(car.Prop);
        t.True(an != null, "the bound carrier gets a clip of the scene");
        var p = A.PoseAt(car, 44.0);
        clips.Apply(car, p, 44.0, 0.04);
        t.True(an!.CurName != "unload", "not yet open before 44.6 s");
        for (double tt = 44.6; tt <= 46.61; tt += 0.04) clips.Apply(car, A.PoseAt(car, tt), tt, 0.04);
        t.Equal("unload", an.CurName, "the hull opens");
        t.Near(2.0, an.Cur!.Time, 0.06, "two seconds into the frame range (frame 50)");
        t.True(an.Cur.Paused, "the frame is the scene's (held, set from its time)");
        // the end: the world's prop keeps the open hull
        A.Moved = true;
        A.Finish(false);
        t.Equal("unload", car.Prop.Anim!.CurName, "after the scene the carrier is open");
        t.Near(4.0, car.Prop.Anim.Cur!.Time, 0.01, "at frame 100");

        // every scene of the base campaign: a bound prop with animation items plays them
        int n = 0;
        var bad = new List<string>();
        foreach (var (id, ss) in Sessions(t, "base"))
            foreach (var (path, sq) in ss.Campaign.Data.Sequences)
            {
                if (sq?["play"] is not JsonObject pj) continue;
                var B = new SeqActors(ss.Campaign, pj.DeepClone());
                var C = new SceneClips(B);
                foreach (var a in B.List.Where(a => a.E == null && a.Prop?.Anim != null))
                    foreach (var it in a.A["items"]!.AsArray().Where(i => i!["k"].Str() is "anim" or "act" && a.Prop!.Anim!.Has(i["anim"].Str())))
                    {
                        double tt = it!["t"].Num() + 0.02;
                        C.Apply(a, B.PoseAt(a, tt), tt, 0.02);
                        n++;
                        if (C.Of(a.Prop!)?.CurName != it["anim"].Str().ToLowerInvariant()) bad.Add($"{id} {TrigUtil.FilePart(path)} {a.A["name"].Str()}: {it["anim"].Str()} not played");
                    }
            }
        foreach (var b in bad.Take(20)) t.Log("  " + b);
        t.Log($"{n} animation items of bound props");
        t.True(bad.Count == 0, $"{bad.Count} items of bound props not played");
    }

    static IEnumerable<(int Id, MissionSession S)> Sessions(TestContext t, string cfg, string tribe = "")
    {
        var c = CampaignTests.LoadCfgPublic(cfg);
        if (c == null) yield break;
        foreach (var e in MissionEntry.LoadIndex(c.Config.MissionsDir))
        {
            if (WorldBuilder.FindMap(c.Config, e.Map) == null) continue;
            MissionSession s;
            try { s = CampaignTests.Session(t, cfg, e.Id, tribe); } catch (SkipException) { continue; }
            yield return (e.Id, s);
        }
    }

    /// <summary>
    /// The clips of a swapped tribe's actors and of the parts: mission 13 as the SEAS (MIRAGE) draws the player's
    /// allosaurus as a Black Widow whose turret rests; no actor of any scene, in any tribe, hangs a part playing an attack
    /// clip that the scene's own data does not give it; the clip mapping never picks an attack for a gesture.
    /// </summary>
    public static void NoAttackLoops(TestContext t)
    {
        // the mapping
        var spider = new HashSet<string> { "attack_front", "standanim", "walk_1", "walk_2", "dying", "hit_reaction", "idle_0", "idle_1", "attack_long" };
        bool Has(string? n) => n != null && spider.Contains(n.ToLowerInvariant());
        t.Equal("walk_2", SceneClips.SceneClip("walk_2", Has), "its own clip");
        t.Equal(null, SceneClips.SceneClip("res_idle_4", Has), "a gesture it lacks: the idle");
        t.Equal(null, SceneClips.SceneClip("talking_0", Has), "a talk gesture it lacks: the idle");
        t.Equal("walk_2", SceneClips.SceneClip("walk_3", Has), "a faster walk: its fastest");
        t.Equal("walk_2", SceneClips.SceneClip("run", Has), "a run: a walk");
        t.Equal("walk_1", SceneClips.SceneClip("swim_1", Has), "a swim on land: the walk");
        t.Equal("attack_front", SceneClips.SceneClip("attack_1", Has), "an attack the scene asks for: its own");
        t.Equal("dying", SceneClips.SceneClip("die_0", Has), "a fall");
        t.Equal("hit_reaction", SceneClips.SceneClip("hit_back", Has), "a hit");

        // mission 13 as the SEAS
        var s = CampaignTests.Session(t, "mirage", 13, "SEAS");
        var A = new SeqActors(s.Campaign, Play(s, "sc_4070.seq"));
        var allo = A.List.First(a => a.A["name"].Str() == "aje_allosaurus_1");
        t.Equal("seas_wehrspinne", allo.Cls, "the player's allosaurus is the SEAS' Black Widow");
        t.True(allo.Parts.Any(p => p.Gfx == "seas_wehrspinne_top" && p.Clip == null), "its turret rests: " + string.Join(", ", allo.Parts));
        t.True(allo.Parts.Any(p => p.Slot == "Dri1" && p.Clip == "balista_stand"), "its gunner stands");
        foreach (var w in A.List.Where(a => a.Cls == "seas_wehrspinne" || a.A["cls"].Str() == "seas_wehrspinne"))
            t.True(w.Parts.All(p => p.Clip == null || !p.Clip.StartsWith("attack")), $"{w.A["name"].Str()}: no attack loop ({string.Join(", ", w.Parts)})");

        // every scene, every tribe (base) and MIRAGE: no part plays an attack clip the scene's data does not name
        var bad = new List<string>(); int parts = 0, swapped = 0;
        foreach (var (cfg, tribes) in new[] { ("base", new[] { "", "Hu", "Aje", "Ninigi" }), ("mirage", new[] { "SEAS" }) })
            foreach (var tribe in tribes)
                foreach (var (id, ss) in Sessions(t, cfg, tribe))
                    foreach (var (path, sq) in ss.Campaign.Data.Sequences)
                    {
                        if (sq?["play"] is not JsonObject pj) continue;
                        foreach (var a in new SeqActors(ss.Campaign, pj).List.Where(x => x.Temp))
                        {
                            bool sw = !string.Equals(a.Cls, a.A["cls"].Str(), StringComparison.OrdinalIgnoreCase);
                            if (sw) swapped++;
                            var named = new HashSet<string>((a.A["attach"]?.AsArray() ?? new JsonArray()).OfType<JsonArray>().Where(x => x.Count > 2).Select(x => x[2].Str().ToLowerInvariant()));
                            foreach (var p in a.Parts)
                            {
                                parts++;
                                if (p.Clip != null && p.Clip.StartsWith("attack", StringComparison.OrdinalIgnoreCase) && (sw || !named.Contains(p.Clip.ToLowerInvariant())))
                                    bad.Add($"{cfg}/{tribe} {id} {TrigUtil.FilePart(path)}: {a.A["name"].Str()} ({a.Cls}) {p.Gfx} plays {p.Clip}");
                            }
                        }
                    }
        foreach (var b in bad.Distinct().Take(30)) t.Log("  " + b);
        t.Log($"{parts} parts of actors drawn for the scenes, {swapped} actors of a swapped tribe");
        t.True(bad.Count == 0, $"{bad.Count} parts with an attack loop");
    }

    /// <summary>
    /// Whose mouth moves: sc_4070 - Bela's talk gestures still while Cole speaks and the other way round; every scene of
    /// the base campaign - the lines go to their characters (a line the scene put on another character goes to that
    /// character when it is in the scene - mission 1's sc_1050 Stina -, else to nobody), and at every moment the jaws a
    /// talk gesture moves are those of actors who speak.
    /// </summary>
    public static void OnlySpeakersTalk(TestContext t)
    {
        var root = Environment.GetEnvironmentVariable("PW_CONTENT") is { Length: > 0 } pc ? pc : "/home/claude/testcontent";
        var dir = Path.Combine(root, "configs", "base", "missions");
        if (!Directory.Exists(dir)) t.Skip("no base missions");
        JsonObject Seqs(string folder) => (Json.ParseFile(Path.Combine(dir, folder, "world.json"))?["sequences"] as JsonObject) ?? new JsonObject();

        // sc_4070
        var sc = Seqs("13_single_13")["Cpn_single_001/single_13/sc_4070.seq"]?["play"]?["actors"] as JsonArray;
        if (sc == null) t.Skip("no sc_4070");
        int Idx(string name) => sc!.Select((a, i) => (a, i)).First(x => x.a!["name"].Str() == name).i;
        int bela = Idx("Bela_s0_0"), cole = Idx("Cole_s0_7");
        var lines = SceneSpeech.Lines(sc);
        t.Equal(4, lines.Count(l => l.Speaker == bela || l.Speaker == cole), "Bela's and Cole's four lines");
        t.True(lines.Where(l => l.Name == "bela").All(l => l.Speaker == bela) && lines.Where(l => l.Name == "cole").All(l => l.Speaker == cole), "each on its speaker");
        double MuteAt(int i, double tt) => SceneSpeech.Mute(lines, i, SeqActors.AnimItemAt(sc![i]!, tt)?["anim"].Str(), tt);
        t.Near(0, MuteAt(bela, 22), 1e-9, "Bela talks during his line (talking_0)");
        t.Near(1, MuteAt(bela, 28.5), 1e-9, "Bela's talking_0 is still during Cole's first line");
        t.Near(1, MuteAt(bela, 36), 1e-9, "Bela's talking_a is still during Cole's second line");
        t.Near(0, MuteAt(cole, 36), 1e-9, "Cole talks during his second line");
        t.Near(1, MuteAt(cole, 32), 1e-9, "Cole's talk gesture is still during Bela's second line");
        t.Near(1, MuteAt(cole, 25.5), 1e-9, "Cole's seq_talk_05 is still before his line");
        t.True(MuteAt(cole, 26.88 - SceneSpeech.Ramp / 2) is > 0 and < 1, "his jaw comes in over the ramp");

        // every scene
        int scenes = 0, said = 0, moved = 0, nobody = 0, stilled = 0;
        var bad = new List<string>();
        foreach (var folder in Directory.GetDirectories(dir).Select(Path.GetFileName).OrderBy(x => x))
            foreach (var (path, sq) in Seqs(folder!))
            {
                if (sq?["play"]?["actors"] is not JsonArray acts) continue;
                var L = SceneSpeech.Lines(acts);
                if (L.Count == 0) continue;
                scenes++; said += L.Count;
                foreach (var l in L)
                {
                    if (l.Speaker < 0) { nobody++; continue; }
                    if (l.Speaker != l.Holder) moved++;
                    var who = SceneSpeech.Speakers.TryGetValue(l.Name, out var w) ? w : null;
                    var ch = SceneSpeech.CharacterOf(acts[l.Speaker]!["cls"].Str());
                    if (who != null && ch != who) bad.Add($"{folder} {TrigUtil.FilePart(path)}: {l.Name}'s line on {acts[l.Speaker]!["name"].Str()}");
                }
                double end = sq["play"]!["end"].Num(60);
                for (double tt = 0; tt <= end; tt += 0.25)
                    for (int i = 0; i < acts.Count; i++)
                    {
                        var clip = SeqActors.AnimItemAt(acts[i]!, tt)?["anim"].Str();
                        if (!SceneSpeech.IsTalkClip(clip)) continue;
                        double m = SceneSpeech.Mute(L, i, clip, tt);
                        if (m >= 1) { stilled++; continue; }
                        if (SceneSpeech.Speaking(L, i, tt) <= 0) bad.Add($"{folder} {TrigUtil.FilePart(path)} {tt:0.00}: {acts[i]!["name"].Str()} talks without a line");
                    }
            }
        // mission 1's sc_1050: Stina's lines (on Cole's actor) go to Stina on her sabre-tooth
        var s1050 = Seqs("01_single_01")["Cpn_single_001/single_01/sc_1050.seq"]?["play"]?["actors"] as JsonArray;
        if (s1050 != null)
            t.True(SceneSpeech.Lines(s1050).Where(l => l.Name == "stina").All(l => l.Speaker >= 0 && s1050[l.Speaker]!["cls"].Str().Contains("stina", StringComparison.OrdinalIgnoreCase)), "sc_1050: Stina speaks her lines");
        foreach (var b in bad.Take(30)) t.Log("  " + b);
        t.Log($"{scenes} scenes with {said} lines: {moved} lines go to another actor of the scene, {nobody} to nobody (off-screen voices, sounds); {stilled} quarter seconds of talk gestures held still");
        t.True(scenes > 50, "the scenes were read");
        t.True(bad.Count == 0, $"{bad.Count} wrong speakers / mouths");
    }
}
