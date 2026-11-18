// Effect models: the original game's effects are models from its GSF archives (almost all in all_effects.gsf)
// placed for a fixed time - their particle emitters do the drawing (Particles), some also play a clip "anim". This
// keeps the placed ones, moves the attached and flying ones along, and removes them when their time is up.
//
// Reference: Data/Base/Scripts/Game/misc/Effects.usl (classes CEffect: SetGFX, SetAnim("anim",1), SetDieInterval),
// the browser remake's docs/GAME_EFFECTS.md. The model names are case-insensitive (the GSF stores them in lower case).
//
// Time: the effects' own clock (Update's dt: the session passes the game's time while it runs and the frame's time
// in a cutscene, which stands the game still - the JS timed them on the world's clock, so a cutscene's effects stood
// until the scene ended).
//
// Ported from remake/src/engine/modelfx.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.View.Anim;
using PwRemake.View.Models;

namespace PwRemake.View.Fx;

public partial class ModelFx : Node3D
{
    /// <summary>a placed effect: its model, until when (ModelFx.Time), what it follows or where it flies</summary>
    public sealed class Entry
    {
        public ModelInstance Obj = null!;
        public double Until;
        public Func<FollowPose?>? Follow;
        public double Offset;
        public string? Key;
        public (Vector3 From, Vector3 To, double T, double Dur)? Fly;
        public bool Freed;
    }

    /// <summary>where a followed thing is now: its link's position (or its own), whether it is drawn; null = gone</summary>
    public readonly record struct FollowPose(Vector3 Pos, bool Visible);

    readonly ModelLibrary _lib;
    readonly List<Entry> _list = new();
    readonly Dictionary<string, Entry> _keyed = new();
    /// <summary>the effects' clock (s)</summary>
    public double Time { get; private set; }
    public int Count => _list.Count;
    public IReadOnlyList<Entry> List => _list;
    public IReadOnlyDictionary<string, Entry> Keyed => _keyed;
    /// <summary>models created and freed so far (the test scene's checks)</summary>
    public int Made { get; private set; }
    public int FreedCount { get; private set; }

    public ModelFx(ModelLibrary lib) { _lib = lib; Name = "ModelFx"; }

    /// <summary>is the effect model there (a converted model with a file)?</summary>
    public bool Has(string? gfx) => !string.IsNullOrEmpty(gfx) && _lib.Template(gfx.ToLowerInvariant(), null, true) != null;

    /// <summary>
    /// a model at a point for `life` seconds. yaw: its turn; party: the owner's colour; anim: the clip to play
    /// (default "anim"; "" none); loop. key: a name to keep it up with (Extend) or stop it with (Stop); a new one with
    /// the same key replaces it.
    /// </summary>
    public Entry? At(string? gfx, Vector3 pos, double life, float? yaw = null, Color? party = null, string? anim = null, bool loop = false, string? key = null)
    {
        if (key != null && Has(gfx)) Stop(key);
        var e = Make(gfx, life, party, anim, loop);
        if (e == null) return null;
        if (key != null) { e.Key = key; _keyed[key] = e; }
        e.Obj.Position = pos;
        if (yaw != null) e.Obj.Rotation = new Vector3(0, yaw.Value, 0);
        AddChild(e.Obj);
        return e;
    }

    /// <summary>
    /// a model that follows something (AddFX + LinkAction): `follow` gives its link's world position each frame (null:
    /// it is gone - the effect too), `offset` metres higher. key: a name to stop it with (Stop); a new one with the
    /// same key replaces it.
    /// </summary>
    public Entry? Attach(Func<FollowPose?> follow, string? gfx, double life, string? key = null, double offset = 0)
    {
        if (key != null) Stop(key);
        var e = Make(gfx, life, null, null, false);
        if (e == null) return null;
        e.Follow = follow; e.Offset = offset; e.Key = key;
        if (key != null) _keyed[key] = e;
        AddChild(e.Obj);
        Place(e);
        return e;
    }

    /// <summary>a model flying from `from` to `to` in T seconds, then gone (the meteor's rock)</summary>
    public Entry? FlyTo(string? gfx, Vector3 from, Vector3 to, double T)
    {
        var e = At(gfx, from, T);
        if (e != null) e.Fly = (from, to, 0, T);
        return e;
    }

    public void Stop(string key) { if (_keyed.Remove(key, out var e)) e.Until = 0; }
    /// <summary>keeps a keyed effect a while longer (poison, heal glows)</summary>
    public bool Extend(string key, double until) { if (!_keyed.TryGetValue(key, out var e) || e.Until <= Time) return false; e.Until = until; return true; }
    public bool HasKey(string key) => _keyed.TryGetValue(key, out var e) && e.Until > Time;

    Entry? Make(string? gfx, double life, Color? party, string? anim, bool loop)
    {
        if (string.IsNullOrEmpty(gfx)) return null;
        var tpl = _lib.Template(gfx.ToLowerInvariant(), null, true);
        if (tpl == null) return null;
        var obj = _lib.Instantiate(tpl, party);
        if (anim != "" && obj.Anim != null)
        {
            // (the clips' tracks of nodes the model does not have - the lower levels of detail - are not drawn: the
            // converter did not export them, and Godot's animation player skips tracks without a node)
            var a = obj.Anim.Pick(anim ?? "anim") ?? FirstClip(tpl);
            if (a != null) obj.Anim.Play(a, new PlayOptions { Loop = loop, Restart = true, Fade = 0 });
        }
        var e = new Entry { Obj = obj, Until = Time + life };
        _list.Add(e);
        Made++;
        return e;
    }

    static string? FirstClip(ModelTemplate t)
    {
        foreach (var k in t.ClipNames.Keys) if (!k.Contains('#')) return k;
        return null;
    }

    void Place(Entry e)
    {
        var p = e.Follow!();
        if (p == null) { e.Until = 0; return; }
        e.Obj.Position = p.Value.Pos + new Vector3(0, (float)e.Offset, 0);
        e.Obj.Visible = p.Value.Visible;
    }

    /// <summary>one frame: dt advances the effects' clock and their clips</summary>
    public void Update(double dt)
    {
        Time += dt;
        if (_list.Count == 0) return;
        bool over = false;
        foreach (var e in _list)
        {
            if (e.Until <= Time) { over = true; continue; }
            if (e.Follow != null) Place(e);
            if (e.Fly is { } fl)
            {
                var (from, to, t, T) = fl;
                t += dt;
                e.Fly = (from, to, t, T);
                e.Obj.Position = from.Lerp(to, (float)Math.Min(1, t / T));
                e.Obj.Rotation = e.Obj.Rotation with { X = e.Obj.Rotation.X + (float)dt * 3 };
            }
            e.Obj.Advance(dt);
        }
        if (!over) return;
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var e = _list[i];
            if (e.Until > Time) continue;
            Free(e);
            _list.RemoveAt(i);
        }
    }

    void Free(Entry e)
    {
        if (e.Freed) return;
        e.Freed = true;
        FreedCount++;
        if (GodotObject.IsInstanceValid(e.Obj)) { e.Obj.GetParent()?.RemoveChild(e.Obj); e.Obj.QueueFree(); }
        if (e.Key != null && _keyed.TryGetValue(e.Key, out var k) && k == e) _keyed.Remove(e.Key);
    }

    public void Clear()
    {
        foreach (var e in _list) Free(e);
        _list.Clear();
        _keyed.Clear();
    }
}
