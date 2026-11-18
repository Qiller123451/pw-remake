// Animation sound events of a model: the GSF sound table, exported by the toolkit into the asset manifest
// (manifest.models[x].sounds: { clipName: [[time, wav, volume, maxHear, group, minFade, pitch], ...] }; data prepared
// before the toolkit wrote minFade and pitch (pw-exporter toolkit-pitch) has the first five). Entries sharing a time are
// alternatives - one of them is played (at random). The wav is an original path; sounds.json "files" maps it to the
// file below assets/snd/ (AudioCore.AnimEvent).
//
// The animation player (who knows a clip's time) calls Fire(clip, t0, t1, ...) with the clip time before and after its
// step; a looping clip that wrapped around fires the end and the start. The callback gets the picked event - usually
// AudioCore.AnimEvent(ev, unit position) for a unit the player can see (world.js animSound), or "!fx" camera shakes.
// Ported from remake/src/game/anim.js setEvents / update (the sound part; pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

public sealed class AnimSounds
{
    /// <summary>clip (lower case) -> its event times, sorted, each with its alternatives.</summary>
    public readonly Dictionary<string, List<(double T, AnimSoundEvent[] Alts)>> Clips = new();

    /// <summary>The table of a model: manifest.models[x].sounds (null: none).</summary>
    public static AnimSounds? FromManifest(JsonNode? sounds)
    {
        if (sounds is not JsonObject o || o.Count == 0) return null;
        var res = new AnimSounds();
        foreach (var (clip, list) in o)
        {
            if (list is not JsonArray a) continue;
            var byT = new List<(double T, List<AnimSoundEvent> L)>();
            foreach (var x in a)
            {
                if (x is not JsonArray e || e.Count < 2) continue;
                var ev = new AnimSoundEvent(e[0].Num(), e[1].Str(), e.Count > 2 ? e[2].Num() : 70, e.Count > 3 ? e[3].Num() : 0, e.Count > 4 ? e[4].Str() : "",
                    e.Count > 5 ? e[5].Num() : 0, e.Count > 6 ? e[6].Num() : 0);
                int k = byT.FindIndex(b => b.T == ev.Time);
                if (k < 0) byT.Add((ev.Time, new List<AnimSoundEvent> { ev })); else byT[k].L.Add(ev);
            }
            res.Clips[clip.ToLowerInvariant()] = byT.OrderBy(b => b.T).Select(b => (b.T, b.L.ToArray())).ToList();
        }
        return res;
    }

    /// <summary>
    /// The slot an animation sound takes (SoundLimits; the remake's - the original had one priority for all): the
    /// fights' clips (attacks, being hit, dying, a building's destruction, the combat sound group) Combat; idle chatter
    /// Ambient - the clips of a unit standing about (task idle / hold, an animal roaming but not walking: feeding,
    /// resting, its stand animation) and a building's working loops; walking and work Activity.
    /// clip: the clip that plays it; group: the event's sound group; task: the unit's task, "building" for a building,
    /// null for other things (a felled tree).
    /// </summary>
    public static SoundPriority Priority(string? clip, string? group, string? task)
    {
        var c = (clip ?? "").ToLowerInvariant();
        if (c.Contains("attack") || c.Contains("hit") || c.Contains("die") || c.Contains("dying") || c.Contains("dest") ||
            c.Contains("fight") || group == "sndgroup_combat")
            return SoundPriority.Combat;
        if (task == "building") return SoundPriority.Ambient;
        if (task is "idle" or "hold" or "roam")
        {
            bool moving = (group ?? "").StartsWith("sndgroup_move", StringComparison.Ordinal) ||
                          c.StartsWith("walk") || c.StartsWith("run") || c.StartsWith("swim") || c.StartsWith("trip") ||
                          c.StartsWith("accel") || c.StartsWith("brake") || c.Contains("turn");
            return moving ? SoundPriority.Activity : SoundPriority.Ambient;
        }
        return SoundPriority.Activity;
    }

    /// <summary>The events of a clip between its times t0 (before the step) and t1 (after): for each event time in
    /// (t0, t1] one of its alternatives goes to `onEvent`. dur: the clip's length (a looping clip that wrapped, t1 &lt;
    /// t0, fires the end then the start); off: where a start / loop / end part begins in the whole clip.</summary>
    public void Fire(string clip, double t0, double t1, double dur, double off, Func<double> rnd, Action<AnimSoundEvent> onEvent)
    {
        if (!Clips.TryGetValue(clip.ToLowerInvariant(), out var list)) return;
        void F(double lo, double hi)
        {
            foreach (var (t, alts) in list)
                if (t - off > lo && t - off <= hi) onEvent(alts[(int)Math.Floor(rnd() * alts.Length)]);
        }
        if (t1 >= t0) F(t0, t1);
        else { F(t0, dur + 1e-3); F(-1, t1); }     // wrapped around (looping clip)
    }
}
