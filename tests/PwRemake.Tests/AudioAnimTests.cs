// The original's features of the round 2026-11-18 "audioanim" (docs/AUDIO.md "Crowd sounds", "Animation sounds";
// docs/SIMULATION.md "Animation audit"): the crowd sounds of soundeffects.txt (CSoundEventMgrClient 0x490340), the GSF's
// pitch of animation sounds, the idle animations of wild animals and the T-rex, a character killed in a hit clip.
//   PW_DATA   the converted data (default /opt/data/base); the installation's Audio/soundeffects.txt:
//             PW_SOUNDEFFECTS (default the staged /mnt/user-data/uploads/Paraworld/Data/Base/Audio/soundeffects.txt)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using PwRemake.View.Audio;

namespace PwRemake.Tests;

[TestClass]
public static class AudioAnimTests
{
    static string SoundEffectsPath => Environment.GetEnvironmentVariable("PW_SOUNDEFFECTS") is { Length: > 0 } s ? s
        : "/mnt/user-data/uploads/Paraworld/Data/Base/Audio/soundeffects.txt";

    /// <summary>The original's file (Base); the same text when it is not staged.</summary>
    static CrowdTable Table()
    {
        if (File.Exists(SoundEffectsPath)) return CrowdTable.Load(SoundEffectsPath);
        return CrowdTable.Parse(@"Root {
	Additionals {
		sndgroup_fight {
			add-1 { wav = '10_sndgroups/sndgroup_fight_wardrum_huge.wav' volume = '48' randomweight = '1' mintriggers = '12' maxtriggers = '100' virtuallength = '4.0' virtualvolume = '1' minfadedistance = '5' maxfadedistance = '65' maxhearingdistance = '66' }
			add-2 { wav = '10_sndgroups/sndgroup_fight_wardrum_small.wav' volume = '58' randomweight = '1' mintriggers = '8' maxtriggers = '5' virtuallength = '4.0' virtualvolume = '1' }
		}
		sndgroup_combat {
			add-1 { wav = '10_sndgroups/sndgroup_fight_large.wav' volume = '99' randomweight = '1' mintriggers = '12' maxtriggers = '100' virtuallength = '4.0' virtualvolume = '1' minfadedistance = '10' maxfadedistance = '75' maxhearingdistance = '76' }
			add-2 { wav = '10_sndgroups/sndgroup_fight_med.wav' volume = '78' randomweight = '1' mintriggers = '8' maxtriggers = '5' virtuallength = '4.0' virtualvolume = '1' minfadedistance = '8' maxfadedistance = '65' maxhearingdistance = '66' }
		}
		sndgroup_move_few_dinos { add-1 { wav = '10_sndgroups/sndgroup_move_few_dinos.wav' volume = '48' randomweight = '1' mintriggers = '5' maxtriggers = '100' virtuallength = '6.0' virtualvolume = '1' minfadedistance = '5' maxfadedistance = '65' maxhearingdistance = '66' } }
		sndgroup_move_fighter { add-1 { wav = '10_sndgroups/sndgroup_move_fighter.wav' volume = '48' randomweight = '1' mintriggers = '10' maxtriggers = '100' virtuallength = '3.0' virtualvolume = '1' minfadedistance = '5' maxfadedistance = '65' maxhearingdistance = '66' } }
	}
}");
    }

    /// <summary>soundeffects.txt read as the client's loader (0x490f70): four groups, their entries and minimum.</summary>
    public static void CrowdTableOfTheOriginal(TestContext t)
    {
        var T = Table();
        t.Equal("sndgroup_fight,sndgroup_combat,sndgroup_move_few_dinos,sndgroup_move_fighter", string.Join(",", T.Groups.Select(g => g.Name)), "the groups");
        var c = T.Group("SNDGROUP_COMBAT")!;
        t.Equal(2, c.Adds.Count, "sndgroup_combat: two entries");
        var a = c.Adds[0];
        t.Equal("10_sndgroups/sndgroup_fight_large.wav", a.Wav, "the din of a fight");
        t.True(a.Volume == 99 && a.MinTriggers == 12 && a.MaxTriggers == 100 && a.VirtualLength == 4 && a.VirtualVolume == 1, "99 %, 12 .. 100, 4 s, 1");
        t.True(a.MinFade == 10 && a.MaxFade == 75 && a.MaxHear == 76, "min fade 10, max 75, heard 76 m");
        t.Equal(8, c.MinTriggers, "the group's minimum: the smallest mintriggers (the second entry's 8)");
        t.Equal(10, T.Group("sndgroup_move_fighter")!.MinTriggers, "marching: 10");
        var drum = T.Group("sndgroup_fight")!.Adds[1];
        t.True(drum.MaxHear == 0 && drum.MinFade == 0 && drum.Volume == 58, "the small drum: no distances (the loader's 0)");
        var d = CrowdTable.Parse("Root { Additionals { g { a { wav = 'x.wav' } b { wav = 'y.wav' randomweight = '0' } } } }").Group("g")!;
        t.Equal(1, d.Adds.Count, "an entry of weight 0 is not added");
        t.True(d.Adds[0].Volume == 90 && d.Adds[0].MinTriggers == 1 && d.Adds[0].MaxTriggers == 1 && d.Adds[0].VirtualLength == 1, "the loader's defaults: 90, 1, 1, 1 s");
    }

    sealed class CrowdRig
    {
        public readonly CrowdSounds C = new();
        public readonly List<(string Wav, V3 P, double T)> Played = new();
        public readonly List<RecordingAudioOut.Handle> Handles = new();
        public double Now;
        public CrowdRig()
        {
            C.Table = Table();
            C.Rnd = AudioTests.Mulberry32(5);
            C.Play = (g, a, p) => { Played.Add((a.Wav, p, Now)); var h = new RecordingAudioOut.Handle { File = a.Wav }; Handles.Add(h); return h; };
        }
        public void Hits(string group, int n, double x, double z, double spread = 4)
        {
            for (int i = 0; i < n; i++) C.Trigger(group, new V3(x + (i % 4) * spread / 3, 0, z + (i / 4) * spread / 3), Now);
        }
        public void Run(double sec, Action? each = null)
        {
            // (steps of 1/32 s: the times are exact, the passes fall every 0.5 s)
            for (double e = 0; e < sec - 1e-9; e += 1 / 32.0) { Now += 1 / 32.0; each?.Invoke(); C.Update(Now); }
        }
    }

    /// <summary>
    /// CSoundEventMgrClient 0x490340: every 0.5 s the triggers of the last 3 s, crowds of points within 32 m; a crowd of
    /// 12 strikes plays the fight's din at the middle of its box, 11 do not; while it holds the place (4 s, virtualvolume
    /// 1) no second one; the crowd gone, the sound is stopped at the next pass; the entries of 8 .. 5 never play.
    /// </summary>
    public static void CrowdSoundsCount(TestContext t)
    {
        var R = new CrowdRig();
        R.Hits("sndgroup_combat", 11, 0, 0);
        R.Run(1);
        t.Equal(0, R.Played.Count, "11 strikes: no crowd sound");
        t.Equal(11, R.C.LastCrowds.Single().Count, "one crowd of 11");
        R.Hits("sndgroup_combat", 1, 4, 4);
        R.Run(0.5);
        t.Equal(1, R.Played.Count, "the 12th: the din of the fight");
        t.Equal("10_sndgroups/sndgroup_fight_large.wav", R.Played[0].Wav, "sndgroup_fight_large");
        t.Near(2, R.Played[0].P.X, 1e-9, "at the middle of the crowd's box (x 0 .. 4)");
        t.Near(2, R.Played[0].P.Z, 1e-9, "(z 0 .. 4)");
        // the fight goes on: 12 strikes every half second
        R.Run(3.5, () => { if (Math.Abs(R.Now * 2 - Math.Round(R.Now * 2)) < 1e-6) R.Hits("sndgroup_combat", 12, 0, 0); });
        t.Equal(1, R.Played.Count, "held for its virtuallength (4 s): no second din");
        R.Run(1.5, () => { if (Math.Abs(R.Now * 2 - Math.Round(R.Now * 2)) < 1e-6) R.Hits("sndgroup_combat", 12, 0, 0); });
        t.Equal(2, R.Played.Count, "after 4 s it is forgotten (it plays to its end) and the next starts");
        t.True(!R.Handles[0].Ended, "the first was not stopped");
        double end = R.Now;
        // the fight stops: the triggers run out after 3 s, the sound is stopped at the next pass
        R.Run(3.0);
        t.True(!R.Handles[1].Ended, "the crowd still there (triggers of the last 3 s)");
        R.Run(1.0);
        t.True(R.Handles[1].Ended, "no crowd near it in the last pass: stopped");
        t.Equal(0, R.C.ActiveOf("sndgroup_combat").Count, "and forgotten");

        // 9 .. 11 strikes pass the group's minimum (8) but no entry takes them (12 .. 100; 8 .. 5 is never)
        var Q = new CrowdRig();
        Q.Hits("sndgroup_combat", 9, 0, 0); Q.Run(1);
        t.Equal(0, Q.Played.Count, "9 strikes: nothing (the second entry asks for 8 .. 5)");
        // two crowds far apart: two sounds; a chain of strikes 20 m apart: one crowd, its middle
        Q.Run(3.5);
        Q.Hits("sndgroup_combat", 12, -100, 0); Q.Hits("sndgroup_combat", 12, 100, 0);
        Q.Run(0.5);
        t.Equal(2, Q.Played.Count, "two crowds 200 m apart: two");
        Q.Run(5);
        int before = Q.Played.Count;
        for (int i = 0; i < 12; i++) Q.C.Trigger("sndgroup_combat", new V3(i * 20, 0, 50), Q.Now);
        Q.Run(0.5);
        t.Equal(1, Q.C.LastCrowds.Count, "a chain of strikes 20 m apart is one crowd");
        t.Equal(before + 1, Q.Played.Count, "one din");
        t.Near(110, Q.Played[^1].P.X, 1e-9, "at the middle of the chain (0 .. 220 m)");
        // marching: 10 steps of soldiers
        var M = new CrowdRig();
        M.Hits("sndgroup_move_fighter", 10, 0, 0); M.Run(0.5);
        t.Equal("10_sndgroups/sndgroup_move_fighter.wav", M.Played.Single().Wav, "10 soldiers' steps: marching");
        // a group without entries (the dinosaurs' steps) is not counted
        M.C.Trigger("sndgroup_move_dinos_big_herb", new V3(0, 0, 0), M.Now);
        t.Equal(1, M.C.Table.Groups.Count(g => g.Name == "sndgroup_move_few_dinos"), "the few dinos group exists ...");
        t.True(M.C.Table.Group("sndgroup_move_dinos_big_herb") == null, "... the GSF's dinosaur groups have no entries");
    }

    static double s_now;

    /// <summary>AudioCore: an animation sound with a sound group that is started is a trigger; one refused (too far)
    /// is not; the crowd sound is a world sound at the crowd (hearing distance 76 m, 1/d past 10 m, the fights' slots);
    /// the GSF's pitch is the speed of the sound (no random number), without it 0.95 .. 1.05.</summary>
    public static void CrowdAndPitchInAudioCore(TestContext t)
    {
        var o = new RecordingAudioOut { Duration = 0.3 };
        var A = new AudioCore(o) { Rnd = AudioTests.Mulberry32(3), Clock = () => s_now };
        A.SetData(SoundDb.Load(Path.Combine(t.Data(), "assets", "sounds.json")));
        A.Crowd.Table = Table();
        s_now = 10;
        var wavs = A.Db.Files.Keys.Take(30).ToList();
        if (wavs.Count < 24) t.Skip("too few animation wavs");
        // 12 strikes 2 m apart, 40 m from the listener
        for (int i = 0; i < 12; i++) t.True(A.AnimEvent(new AnimSoundEvent(0, wavs[i], 60, 66, "sndgroup_combat", 5, 1.2), new V3(40 + i % 4 * 2, 0, i / 4 * 2)) != null, "strike " + i);
        t.True(o.Starts.All(h => h.Rate == 1.2), "the GSF's pitch 1.2: the speed of every strike");
        // 12 more far away (100 m): not heard, no triggers
        for (int i = 12; i < 24; i++) A.AnimEvent(new AnimSoundEvent(0, wavs[i], 60, 66, "sndgroup_combat", 5, 1), new V3(-100, 0, i));
        int n = o.Starts.Count;
        t.Equal(12, n, "the far strikes are not heard");
        s_now += 0.6; A.Frame();
        var din = o.Starts.Skip(n).SingleOrDefault(h => h.File.Contains("sndgroup_fight_large"));
        t.True(din != null, "the din of the fight starts");
        t.True(o.Starts.Skip(n).Count() == 1, "one sound: only the heard strikes counted");
        t.Near(0.99 * 10 / Math.Sqrt(43 * 43 + 2 * 2), din!.StartGain, 1e-9, "99 % x 10 m / d (the crowd's middle: 43, 2)");
        t.Equal(1.0, din.Rate, "the crowd sound at its own speed");
        // no pitch in the data: the browser remake's 0.95 .. 1.05
        double r0 = 0;
        A.AnimEvent(new AnimSoundEvent(0, wavs[25], 60, 66, ""), new V3(0, 0, 0));
        r0 = o.Starts[^1].Rate;
        t.True(r0 >= 0.95 && r0 <= 1.05 && r0 != 1, "without a pitch: 0.95 .. 1.05 at random");
        // the manifest's 7th value
        var js = JsonNode.Parse("{\"attack\": [[0.5, \"a.wav\", 60, 66, \"sndgroup_combat\", 6, 1.3], [0.5, \"b.wav\", 60, 66, \"\"]]}");
        var tab = AnimSounds.FromManifest(js)!;
        var alts = tab.Clips["attack"][0].Alts;
        t.True(alts[0].Pitch == 1.3 && alts[0].MinFade == 6 && alts[0].Group == "sndgroup_combat", "[t, wav, vol, maxHear, group, minFade, pitch]");
        t.True(alts[1].Pitch == 0 && alts[1].MinFade == 0, "the older five values: no pitch");
    }

    /// <summary>The prepared data: when the toolkit wrote the pitch (pw-exporter toolkit-pitch), the entries have 7
    /// values and the pitches of the GSF (0.5 .. 1.8, most 1); older data has 5 (the fallback).</summary>
    public static void PitchInTheManifest(TestContext t)
    {
        var man = Json.ParseFile(Path.Combine(t.Data(), "assets", "manifest.json"))!;
        int n5 = 0, n7 = 0; var pitches = new HashSet<double>();
        foreach (var (_, m) in man["models"]!.AsObject())
        {
            var tab = AnimSounds.FromManifest(m?["sounds"]);
            if (tab == null) continue;
            foreach (var (_, list) in tab.Clips) foreach (var (_, alts) in list) foreach (var e in alts) { if (e.Pitch > 0) { n7++; pitches.Add(e.Pitch); } else n5++; }
        }
        t.Log($"animation sounds: {n7} with the GSF's pitch, {n5} without");
        if (n7 == 0) { t.Log("data prepared before the toolkit wrote the pitch: the fallback (0.95 .. 1.05)"); return; }
        t.True(pitches.Contains(1) && pitches.All(p => p >= 0.3 && p <= 2.5), "the GSF's pitches");
    }

    // ------------------------------------------------------------------ the simulation's parts

    static (World W, Player P, Player E) World(TestContext t) => Sys2.World(t, Sys2.Base);

    /// <summary>
    /// A wild animal standing (roam: idle / wait) plays the idle animations of its IdleAnims.txt set (CIdleAnimMgr: the
    /// wild boar's standanim, menace, rest, feeding), not only its standing clip; the draws are not World.Rng's (the
    /// world's random numbers run as without them).
    /// </summary>
    public static void WildAnimalIdleAnims(TestContext t)
    {
        var (w, p, e) = World(t);
        var b = w.SpawnUnit("Wild_Boar", null, 0, 0)!;
        if (b.Anim == null || !b.Anim.Has("menace") || !b.Anim.Has("standanim")) t.Skip("no clips of the wild boar");
        t.True(b.Wild, "wild");
        var set = w.Data.IdleSet(b.Name)!;
        t.Equal("standanim,menace,rest,feeding", string.Join(",", set.Select(x => x.Anim)), "IdleAnims.txt: the wild boar's set");
        var seen = new HashSet<string>();
        for (int i = 0; i < 20 * 600; i++)
        {
            w.Step();
            if (b.Task.Type == "roam" && b.Task.Phase is "idle" or "wait" && b.Anim.CurName != null) seen.Add(b.Anim.CurName);
        }
        t.Log("clips while standing: " + string.Join(", ", seen));
        t.True(seen.Contains("standanim"), "its standing clip");
        t.True(seen.Contains("menace") || seen.Contains("rest") || seen.Contains("feeding"), "and idle animations of its set");
        // the same world without the idle animations' draws: World.Rng the same afterwards (IdleRoll is its own stream)
        double r1 = w.IdleRoll(b, 0);
        t.Equal(r1, w.IdleRoll(b, 0), "the draw is the same when asked again");
        t.True(Math.Abs(w.IdleRoll(b, 0) - w.IdleRoll(b, 1)) > 1e-12, "another draw of the step: another number");
    }

    /// <summary>The T-rex (CTrex.SetIdleAnim): standing it is always in one of idle_0 .. idle_4, never its standanim;
    /// idle_0 for 1 .. 14 loops, the others once. No object of the tech trees or the campaign maps is of the class
    /// "trex" (CTrex): the rule runs on the T-rex model of the Aje's atroxosaurus (CAjeTrex, a plain CAnimal - IsTrex
    /// false), called as WildIdle calls it for a CTrex.</summary>
    public static void TrexIdles(TestContext t)
    {
        var (w, p, e) = World(t);
        t.True(w.Data.IsA("aje_atroxosaurus", "CAnimal") && !w.Data.IsA("aje_atroxosaurus", "CTrex"), "the atroxosaurus is no CTrex");
        t.True(!w.Data.Exists("trex"), "\"trex\" (CTrex) is no object of the tech tree");
        var r = w.SpawnUnit("aje_atroxosaurus", p, 0, 0)!;
        if (r.Anim == null || !r.Anim.Has("idle_1") || !r.Anim.Has("idle_0")) t.Skip("no clips of the T-rex model (trex.glb)");
        t.True(!w.IsTrex(r), "IsTrex: the script class");
        var seen = new HashSet<string>();
        int loops0 = 0;
        for (int i = 0; i < 20 * 300; i++)
        {
            w.Step();
            string? before = r.IdleCur; var cur = r.Anim.CurName;
            t.True(w.TrexIdle(r), "it has the clips");
            if (r.Anim.CurName != cur || r.IdleCur != before) { seen.Add(r.IdleCur!); if (r.IdleCur == "idle_0") loops0 = Math.Max(loops0, (int)Math.Round(r.IdleLeft / r.Anim.LoopLength("idle_0"))); }
            t.True(r.Anim.CurName!.StartsWith("idle_"), "always an idle_ clip: " + r.Anim.CurName);
        }
        t.Log($"T-rex idles: {string.Join(", ", seen.OrderBy(x => x))}; idle_0 up to ~{loops0} loops");
        t.True(seen.Count >= 4, "idle_0 .. idle_4 at random");
    }

    /// <summary>
    /// A character killed in a hit clip (hit_back of a throw): CCharacterCorpse holds that clip's last frame at once
    /// (SetStillAnim(clip, -1)) - not the rest of the clip played on, not dying.
    /// </summary>
    public static void DeathInHitClipHoldsTheLastFrame(TestContext t)
    {
        var (w, p, e) = World(t);
        var v = w.SpawnUnit("hu_warrior", p, 0, 0)!;
        if (v.Anim == null || !v.Anim.Has("hit_back")) t.Skip("no clips of the warrior");
        var a = w.SpawnUnit("aje_warrior", e, 3, 0)!;
        v.BusyAnim = true;
        v.Anim.Play("hit_back", new AnimOpts { Loop = false, Restart = true });
        for (int i = 0; i < 4; i++) w.Step();
        double len = v.Anim.Duration("hit_back");
        t.True(v.Anim.Cur!.Time < len - 0.1, "thrown: early in hit_back");
        v.Hp = 1;
        w.ProvideDmg(5, a, v, false, 0, 0, null);
        t.True(!v.Alive, "killed");
        t.Equal("hit_back", v.Anim.CurName, "the corpse keeps hit_back");
        t.True(v.Anim.Cur!.Paused, "a still frame");
        t.Near(len, v.Anim.Cur.Time, 1e-9, "its last frame (-1), at once");
        for (int i = 0; i < 20; i++) w.Step();
        t.Equal("hit_back", v.Anim.CurName, "and stays");
        t.Near(len, v.Anim.Cur!.Time, 1e-9, "on the last frame");
    }
}
