// The clips of a cutscene's bound world objects while the scene plays (the view's cutscene player,
// game/src/App/SessionSequenceStage.cs, draws them with these; docs/GAME_SESSION.md "Cutscenes").
//
// While a timeline plays the world stands still (docs/SEQUENCE_FORMAT.md §9.2), so the world's own clips (AnimCtl) do not
// run; the scene's actors animate on the scene's time instead. Every bound object with clips gets its own AnimCtl here -
// a unit an empty one (its animation items, walks, else its idle), any other object (a building: a gate, the Scorpion,
// a garage) a copy of the world's clip of the moment, which the scene runs on and whose animation items it plays. The
// world's AnimCtl is left alone (docs/SAVES.md); what the scene leaves is the world's at its end (SeqActors.Finish).
//
// Without a clip of their own the buildings were drawn with the world's standing clip advanced by the frame's real time
// and put back on the world's time 0.25 s later (EntityViews: an actor is "live"): mission 16's three end boss gates,
// opening at the start of sc_7050, jumped between half open and closed four times a second.
//
// Animation items (§8.1): an ItemAnim ("anim") plays a frame range of its clip, `repeat` times, and holds its last frame
// (SeqActors.FrameTime: the clip is held, its time set from the scene's - the view keeps the pose); an ItemAnimAct ("act")
// plays the clip (looping while it lasts). Ported from remake/src/ui/cutscene.js apply (pw-exporter 98c7703), which ran the
// world's own clips and played whole clips for both kinds.
//
// Bound props (a map object without rules that the scene binds: mission 13's sc_4070 binds its carrier seas_carrier_10
// - owner -1 - to the ownerless seas_carrier_7, which the map places as scenery) get a copy of their clip too and play
// their animation items: the carrier's "unload" (frames 0..100 from 44.6 s, the bow opening while the marksmen walk out
// onto the pier) was not played - only entities had clips here - and the hull stayed shut (2026-11-12).
//
// A clip the actor's model lacks (an actor of the player drawn or bound as another tribe's class: the scene names the
// clips of the class it was made with) is mapped to the model's own of the same kind (SceneClip: a walk to a walk of
// that speed, a swim, a fall, a hit; an attack only when the scene attacks), else the idle.
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Campaign;

public sealed class SceneClips
{
    /// <summary>by bound entity or prop</summary>
    readonly Dictionary<object, AnimCtl> _anims = new();
    readonly HashSet<object> _started = new();
    readonly HashSet<SeqActors.Actor> _posed = new();

    /// <summary>a clip of its own for every bound actor with clips</summary>
    public SceneClips(SeqActors actors)
    {
        foreach (var A in actors.List)
        {
            if (A.E?.Anim is { } wa && !_anims.ContainsKey(A.E)) _anims[A.E] = A.E is Unit ? new AnimCtl(wa.Clips, null) : CopyOf(wa);
            else if (A.E == null && A.Prop?.Anim is { } pa && !_anims.ContainsKey(A.Prop)) _anims[A.Prop] = CopyOf(pa);
        }
    }

    /// <summary>the clip an actor is drawn with while the scene plays (null: not one of the scene's)</summary>
    public AnimCtl? Of(Entity e) => _anims.TryGetValue(e, out var a) ? a : null;
    /// <summary>the clip a bound prop is drawn with while the scene plays (null: not one of the scene's)</summary>
    public AnimCtl? Of(Prop p) => _anims.TryGetValue(p, out var a) ? a : null;
    public IEnumerable<Entity> Entities { get { foreach (var k in _anims.Keys) if (k is Entity e) yield return e; } }
    public IEnumerable<Prop> Props { get { foreach (var k in _anims.Keys) if (k is Prop p) yield return p; } }

    /// <summary>a copy of a world clip's state (the scene goes on from it)</summary>
    public static AnimCtl CopyOf(AnimCtl w)
    {
        var c = new AnimCtl(w.Clips, null);
        if (w.Cur is { } wc && c.Start(wc.Clip, wc.Loop, wc.TimeScale, 0, w.Phase) is { } a)
        {
            c.CurName = w.CurName; c.Ts = w.Ts;
            a.Time = wc.Time; a.Paused = wc.Paused; a.Enabled = wc.Enabled; a.Clamp = wc.Clamp;
        }
        return c;
    }

    /// <summary>
    /// One frame of a bound actor at scene time t (dt: the frame's scene seconds): the clip of its animation item, a walk
    /// of the walk's speed or the idle (units), else what it has (other objects).
    /// </summary>
    public void Apply(SeqActors.Actor A, SeqActors.Pose p, double t, double dt)
    {
        object? key = (object?)A.E ?? A.Prop;
        if (key == null || !_anims.TryGetValue(key, out var an)) return;
        var cur = SeqActors.AnimItemAt(A.A, t);
        var eu = A.E as Unit;
        string? want = null; bool loop = true, restart = false; double ts = 1;
        if (cur != null && SceneClip(cur["anim"].Str(), an.Has) is { } sc) { want = sc; restart = _started.Add(cur); loop = !(cur["k"].Str() == "anim" && cur["rep"].Num(-1) == 1); }
        else if (eu != null && p.Walk != null)
        {
            int sp = (int)p.Walk["speed"].Num(1);
            want = an.Pick("walk_" + sp, "walk_" + Math.Max(0, sp - 1), eu.WalkAnim, "walk_1");
            ts = WalkTs(eu, want, sp, p.V);
        }
        else if (eu != null) want = eu.IdleAnim;
        double? ft = cur != null && want != null && string.Equals(want, SceneClip(cur["anim"].Str(), an.Has), StringComparison.OrdinalIgnoreCase) ? SeqActors.FrameTime(cur, an.Duration(want), t) : null;
        if (ft is double ftv)
        {
            // a frame range: the clip once, held, its time the scene's
            if (restart || an.CurName != want!.ToLowerInvariant() || an.Cur == null) an.Play(want, new AnimOpts { Loop = false, Restart = true });
            if (an.Cur is { } ac) { ac.Time = ftv; ac.Paused = true; }
            _posed.Add(A);
            return;
        }
        if (_posed.Remove(A) && want != null) restart = true;      // (after a held pose the clip runs again)
        if (want != null && (an.CurName != want.ToLowerInvariant() || restart || an.Ts != ts)) an.Play(want, new AnimOpts { Loop = loop, Restart = restart, Ts = ts });
        if (dt > 0) an.Update(dt);
    }

    /// <summary>
    /// The time scale of a walk clip at the walk's speed v (m/s): the clip's own speed is its gait's (the class's speed
    /// table, as the world's walks: Unit.MoveAnim), so the feet keep to the ground - the scenes' walks run at the
    /// speeds the editor recorded, and a clip at its own pace slid the heroes over the ground. 1 when not known; within
    /// 0.4 .. 1.8 as the world's.
    /// </summary>
    public static double WalkTs(Unit u, string? clip, int speed, double v)
    {
        if (v <= 0.05 || clip == null) return 1;
        double own = u.Gaits.TryGetValue(clip.ToLowerInvariant(), out var g) && g > 0 ? g : u.GaitSpeed(Math.Max(1, speed));
        return own > 0 ? Math.Clamp(v / own, 0.4, 1.8) : 1;
    }

    static readonly Regex Gait = new(@"^(walk|run|swim|hump)[a-z]*_?(\d)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The clip a model plays for a scene's animation item that names `want` (has: the model's clips): that clip when it
    /// has it; else one of its own of the same kind - a walk / run / swim of that speed or the next slower, a fall, a hit,
    /// an attack (only for an item that attacks) -; else null: the actor's idle. A scene names the clips of the class it
    /// was made with; in a mission played with another tribe the player's actors are that tribe's (SeqActors.Look,
    /// Campaign.OwnClass), whose models lack most of them (mission 13's Dustrider warriors' res_idle_*, the
    /// allosaurus' walk_2 on the SEAS Black Widow).
    /// </summary>
    public static string? SceneClip(string? want, Func<string?, bool> has)
    {
        if (string.IsNullOrEmpty(want)) return null;
        if (has(want)) return want;
        var lc = want.ToLowerInvariant();
        string?[] alts;
        var g = Gait.Match(lc);
        if (g.Success)
        {
            int n = g.Groups[2].Success ? g.Groups[2].Value[0] - '0' : g.Groups[1].Value == "run" ? 3 : 1;
            var l = new List<string?>();
            bool swim = g.Groups[1].Value == "swim";
            for (int i = Math.Max(1, n); i >= 1; i--) { l.Add((swim ? "swim_" : "walk_") + i); l.Add((swim ? "walk_" : "swim_") + i); }
            l.Add(swim ? "swim" : "walk");
            alts = l.ToArray();
        }
        else if (lc.StartsWith("attack") || lc.Contains("_attack") || lc.StartsWith("strike") || lc.StartsWith("shoot")) alts = new[] { "attack_front", "attack_1", "attack_short", "attack_long", "attack" };
        else if (lc.StartsWith("dying") || lc.StartsWith("die") || lc.StartsWith("death")) alts = new[] { "dying", "die", "death", "dying_0", "die_0" };
        else if (lc.StartsWith("hit")) alts = new[] { "hit_reaction", "hit_back", "hit" };
        else return null;
        foreach (var a in alts) if (has(a)) return a;
        return null;
    }
}
