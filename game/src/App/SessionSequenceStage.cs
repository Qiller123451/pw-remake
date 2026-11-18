// The cutscenes' world part (the mission UI's ISequenceStage, docs/MISSION_UI.md): the camera of an in-engine cutscene
// - the cut covering the moment picks a camera whose position, turn and field-of-view keys are evaluated as the
// client does (segment, trackValue, viewDir: types linear / cosine / smoothstep / Catmull-Rom / cardinal / Hermite,
// slerp), looking along its local +y with the world's up, shaken by the shake items - the actors (bound world objects
// put where the scene has them, their animation items played; an actor nothing in the world matches drawn for the
// scene only, with its attachments), the attribute items hiding and showing them; the line scenes' camera easing to
// the scene's point in 1.6 s; at the end the camera back with the player.
// While a timeline plays the world stands still (the session is paused); the actors' clips run on the scene's time.
// The view does not change the world (docs/SAVES.md): the actors' clips are the stage's own AnimCtls (Sim/Campaign
// SceneClips: units and buildings - a gate goes on from the world's clip; an ItemAnim plays its frame range; EntityViews.AnimOf),
// the poses it puts them on are overwritten at the scene's end by the world's own result of the scene (SeqActors.Finish
// - the idle clip, the end pose, snap back), applied with the SceneEndCommand that ends the scene (TimelineRequest: this
// scene ran its timeline).
// The effect items: the effect model of the item's path at its place for its length (View/Fx WorldFx.CutsceneEffect).
// Lip sync: a speech item's .lsd (next to its .mp3) turns the speaker's jaw by the weights of the phonemes on frame 1
// of the "faceanim" clip, after the body clip (UI/Mission/Logic/LipSync.cs); the speaker is the line's character
// (UI/Mission/Logic/SceneSpeech.cs: a line the scene put on another actor goes to its character or to nobody), and a
// talk gesture moves the jaw only while its actor speaks (SceneSpeech.Mute: else the jaw is held at its rest pose).
// Bound props play their animation items too (SceneClips: sc_4070's carrier opens its hull). Day time items: the scene's time of day
// and weather (Day) - the session uses them for the ambience and the weather drawn while the scene plays; the world's
// clock is not touched. Hidden while the scene plays: the units inside the scene's region (sequence flag 4,
// play.hide_region) and the bound actors and props an attribute item hides (EntityViews.Hide).
// Ported from remake/src/ui/cutscene.js (Cutscene start / update / apply / camera / lip / jaw / dayItems, the track
// helpers) and remake/src/ui/mission.js (the line scenes' CAM_EASE) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using PwRemake.UI.Mission.Logic;
using PwRemake.View.Entities;
using PwRemake.View.Models;
using PwRemake.View.Anim;

namespace PwRemake.App;

public sealed class SessionSequenceStage : ISequenceStage
{
    const double CAM_EASE = 1.6;
    readonly GameSession S;
    (double X0, double Z0, double X1, double Z1, double T)? _ease;
    SeqActors? _actors;
    readonly Dictionary<SeqActors.Actor, (ModelInstance M, string? Idle)> _temp = new();
    readonly HashSet<object> _started = new();
    /// <summary>actors drawn for the scene only whose clip the scene holds on a frame (an ItemAnim's range) - the next clip
    /// starts afresh</summary>
    readonly HashSet<SeqActors.Actor> _posed = new();
    /// <summary>actors drawn for the scene only that hang on another one's link (SeqActors.LinkedTo)</summary>
    readonly HashSet<SeqActors.Actor> _linked = new();
    float _fov0;
    /// <summary>a timeline's camera is on (the session does not move the camera itself)</summary>
    public bool OwnsCamera { get; private set; }
    /// <summary>the entities the scene animates (their clips run on the scene's time)</summary>
    public readonly HashSet<Entity> Actors = new();
    /// <summary>their clips while the scene plays (units and buildings; the world's AnimCtl of the entity is left alone)</summary>
    SceneClips? _clips;
    /// <summary>the clips an actor is drawn with (EntityViews.AnimOf)</summary>
    public AnimCtl? AnimOf(Entity e) => _clips?.Of(e);
    /// <summary>the sequence whose timeline the stage played last (SceneEndCommand.Timeline: its actors were moved)</summary>
    public PwRemake.Sim.Scripting.SequenceRequest? TimelineRequest { get; private set; }
    /// <summary>the time of day and the weather of the scene's day time items (null: none has begun; the world's)</summary>
    public (double Tod, int Weather, double Intensity)? Day { get; private set; }
    /// <summary>units of the scene's region (hide_region) and actors / props an attribute item hides: not drawn</summary>
    readonly HashSet<object> _hidden = new(), _regionHidden = new();
    /// <summary>is this world object (an entity or a prop) hidden by the scene? (EntityViews.Hide)</summary>
    public bool Hides(object o) => _regionHidden.Contains(o) || _hidden.Contains(o);
    /// <summary>how many world objects the scene hides now (tests)</summary>
    public int HiddenCount => _regionHidden.Count + _hidden.Count;
    /// <summary>the jaws of the actors that speak or play a talk gesture (by actor; null: the model has no jaw)</summary>
    readonly Dictionary<SeqActors.Actor, Lip?> _lips = new();
    readonly HashSet<JsonNode> _said = new();
    /// <summary>the scene's spoken lines with their speakers (SceneSpeech.Lines)</summary>
    List<SceneSpeech.Line> _lines = new();
    readonly Dictionary<SeqActors.Actor, int> _index = new();
    /// <summary>the clip a bound prop is drawn with (EntityViews.PropAnimOf; null: the world's)</summary>
    public AnimCtl? AnimOf(Prop p) => _clips?.Of(p);

    public SessionSequenceStage(GameSession s) { S = s; }

    public void Begin(CinePlay c)
    {
        _started.Clear(); _posed.Clear();
        S.UpdateFow(true);           // what the engine revealed for the scene shows although the game stands still
        var cam = S.Camera;
        if (c.Player == null)
        {
            // a line scene: the camera eases to the scene's point
            var to = c.Opts.Camera is { } mp && !mp.IsZero ? S.Mission!.Campaign.ToGame(mp.X, mp.Y) : ((double, double)?)null;
            _ease = (cam.X, cam.Z, to?.Item1 ?? cam.X, to?.Item2 ?? cam.Z, to != null ? 0 : CAM_EASE);
            return;
        }
        _fov0 = S.Renderer.Camera.Fov;
        _actors = S.Mission?.Host.SeqPlay?.Actors;
        TimelineRequest = c.Opts.Request ?? S.Mission?.Host.SeqPlay?.Request;
        if (_actors != null)
        {
            foreach (var A in _actors.List)
            {
                if (A.E != null) { Actors.Add(A.E); continue; }
                if (!A.Temp) continue;
                // its class (the player's tribe's equivalent in a swapped mission: SeqActors.Look); a class only the
                // cutscenes use and the class files do not name is drawn with the model of its name (mission 1's
                // NS1050_stina_eusmilus in sc_1050: tools/prepare/cutscene_models.py)
                var name = SeqActors.ModelOf(S.World, A.Cls.Length > 0 ? A.Cls : A.A["cls"].Str());
                if (name == null || !EntityViews.HasFile(S.Models, name)) continue;
                var m = S.Models.Instantiate(name.ToLowerInvariant());
                if (m == null) continue;
                // the models on its links (the spear in HndR, the buckler on Shld, a rider on Ride with ride_idle_0)
                var parts = new List<ModelInstance?>();
                foreach (var pt in A.Parts)
                {
                    var host = pt.Parent >= 0 && pt.Parent < parts.Count ? parts[pt.Parent] : m;
                    var o = host != null && EntityViews.HasFile(S.Models, pt.Gfx) ? host.Attach(pt.Gfx, pt.Slot) : null;
                    if (o != null && pt.Clip != null && o.Anim?.Has(pt.Clip) == true) o.Play(pt.Clip);
                    parts.Add(o);
                }
                S.Entities.AddChild(m);
                var idle = m.Anim?.Pick("standanim", "idle_0", "idle");
                if (idle != null) m.Play(idle);
                _temp[A] = (m, idle);
            }
            // an actor the scene links to another one's link at its start (the transport turtle's shell on mission 2's
            // turtle): hung there, it moves with it
            foreach (var A in _actors.List)
            {
                if (A.LinkedTo is not { } P || !_temp.TryGetValue(A, out var ch)) continue;
                if (!_temp.TryGetValue(P, out var par) || par.M.FindLink(A.LinkSlot) is not Node3D link)
                {
                    Log.For("UI.Mission").Debug($"cutscene actor {A.A["name"].Str()}: linked to {P.A["name"].Str()} at {A.LinkSlot} - not drawn there (a world object, or no such link)");
                    continue;
                }
                ch.M.GetParent()?.RemoveChild(ch.M);
                link.AddChild(ch.M);
                ch.M.Transform = Transform3D.Identity;
                ch.M.ToLinkFrame();
                _linked.Add(A);
            }
            _clips = new SceneClips(_actors);
            HideRegion(_actors);
            _index.Clear();
            for (int i = 0; i < _actors.List.Count; i++) _index[_actors.List[i]] = i;
            _lines = SceneSpeech.Lines(_actors.Play["actors"] as JsonArray);
        }
        OwnsCamera = true;
    }

    /// <summary>the region's other units are not shown while the scene plays (sequence flag 4: play.hide_region)</summary>
    void HideRegion(SeqActors actors)
    {
        var P = actors.Play;
        if (P["hide_region"].Bool() != true || P["region"] is not JsonArray region || region.Count == 0 || S.Mission == null) return;
        var Cm = S.Mission.Campaign;
        bool Inside(double x, double z)
        {
            var (mx, my) = Cm.ToMap(x, z);
            foreach (var r in region)
                if (r != null && mx >= r["x"].Num() && mx <= r["x"].Num() + r["w"].Num() && my >= r["y"].Num() && my <= r["y"].Num() + r["h"].Num()) return true;
            return false;
        }
        var mine = new HashSet<object>(actors.List.Select(A => (object?)A.E ?? A.Prop).Where(o => o != null)!);
        foreach (var u in S.World.Units)
            if (u.Alive && !mine.Contains(u) && Inside(u.Pos.X, u.Pos.Z)) _regionHidden.Add(u);
    }

    public void Update(CinePlay c, double dt)
    {
        if (_ease is not var (x0, z0, x1, z1, t) || t >= CAM_EASE) return;
        t = Math.Min(CAM_EASE, t + dt);
        double k = t / CAM_EASE, e = k * k * (3 - 2 * k);
        S.LookAt(x0 + (x1 - x0) * e, z0 + (z1 - z0) * e);
        _ease = (x0, z0, x1, z1, t);
    }

    public void Timeline(CinePlay c, SequenceTimeline play, double t, double dt)
    {
        try { Camera(play, t); } catch (Exception e) { Log.For("UI.Mission").WarnOnce("cam:" + c.Seq?.Id, $"cutscene camera: {e.Message}"); }
        Day = SceneDayTime.At(play.Items, t);
        if (_actors != null) ApplyActors(t, dt);
    }

    public void Effect(TimelineItem fx)
    {
        if (S.Effects == null || S.Mission == null || fx.Pos == null || fx.Pos.Length < 2) return;
        var (x, z) = S.Mission.Campaign.ToGame(fx.Pos[0], fx.Pos[1]);
        double y = fx.Pos.Length > 2 ? fx.Pos[2] : S.World.Height(x, z);
        S.Effects.CutsceneEffect(fx.Path, new Vector3((float)x, (float)y, (float)z), fx.Len);
    }

    public void End(CinePlay c)
    {
        _ease = null;
        if (OwnsCamera) { S.Renderer.Camera.Fov = _fov0; OwnsCamera = false; }
        foreach (var (A, L) in _lips) L?.Restore();
        _lips.Clear(); _said.Clear(); _lines = new(); _index.Clear();
        foreach (var (m, _) in _temp.Values) m.QueueFree();
        _temp.Clear(); _linked.Clear();
        _hidden.Clear(); _regionHidden.Clear();
        Day = null;
        // (the actors' idle clip and their end pose: the world's, at the scene's end - SeqActors.Finish)
        Actors.Clear();
        _clips = null;
        _actors = null;
        S.Camera.Update(0);
    }

    // ------------------------------------------------------------------------------------------- actors
    void ApplyActors(double t, double dt)
    {
        var W = S.World;
        foreach (var A in _actors!.List)
        {
            var p = _actors.PoseAt(A, t);
            double y = p.Ground ? W.Height(p.X, p.Z) : p.Mz;
            var hid = _actors.Hidden(A, t);
            ModelInstance? tm = _temp.TryGetValue(A, out var tv) ? tv.M : null;
            if (A.E is Unit u) { u.Pos = new V3(p.X, p.Ground ? W.Height(p.X, p.Z) : p.Mz, p.Z); u.Heading = p.H; }
            else if (A.Prop != null) { A.Prop.Pos = new V3(p.X, y, p.Z); A.Prop.Rot = p.H; }
            else if (tm != null && _linked.Contains(A)) { }                 // (on its link)
            else if (tm != null) { tm.Position = new Vector3((float)p.X, (float)y, (float)p.Z); tm.Rotation = new Vector3(0, (float)p.H, 0); }
            if (tm != null) tm.Visible = hid != true;
            // (a bound world object an attribute item hides or shows: cutscene.js obj.visible = !hid)
            object? wo = (object?)A.E ?? A.Prop;
            if (wo != null && hid != null) { if (hid.Value) _hidden.Add(wo); else _hidden.Remove(wo); }
            // the animation items: the clip of the moment (an ItemAnim's frame range: SeqActors.FrameTime), a walk of
            // its speed, else the idle (units; a building or a prop keeps the clip it has - SceneClips; an actor drawn
            // for the scene only, the same on its model's own clips). A clip the model lacks (a swapped tribe's actor):
            // its own of the same kind, else the idle (SceneClips.SceneClip) - never an attack the scene does not ask for
            if (A.E != null || A.Prop != null) _clips?.Apply(A, p, t, dt);
            else if (tm?.Anim is { } va)
            {
                var cur = SeqActors.AnimItemAt(A.A, t);
                var sc = cur != null ? SceneClips.SceneClip(cur["anim"].Str(), va.Has) : null;
                string? want = null; bool loop = true, restart = false;
                if (sc != null) { want = sc; restart = _started.Add(cur!); loop = !(cur!["k"].Str() == "anim" && cur["rep"].Num(-1) == 1); }
                else if (p.Walk != null) { int sp = (int)p.Walk["speed"].Num(1); want = va.Pick("walk_" + sp, "walk_" + Math.Max(0, sp - 1), "walk_1"); }
                else want = tv.Idle;
                double? ft = sc != null && want == sc ? SeqActors.FrameTime(cur!, va.Duration(want), t) : null;
                if (ft is double ftv)
                {
                    if (restart || va.CurName != want!.ToLowerInvariant()) va.Play(want, new PlayOptions { Loop = false, Restart = true });
                    va.Pose(ftv);
                    _posed.Add(A);
                }
                else
                {
                    if (_posed.Remove(A) && want != null) restart = true;
                    if (want != null && (va.CurName != want.ToLowerInvariant() || restart)) va.Play(want, new PlayOptions { Loop = loop, Restart = restart });
                    if (dt > 0) va.Update(dt);
                }
                if (dt > 0) foreach (var o in tm.Attached) o.Advance(dt);     // (its riders' clips)
            }
        }
        // speech (after every body clip of the frame): the lip-sync layers of a line from its start on its speaker
        // (SceneSpeech: the line's character, not always the actor whose item it is); the jaws - a talk gesture still
        // while its actor does not speak, the lip layers on top
        foreach (var l in _lines)
        {
            if (l.T > t || !_said.Add(l.Item) || l.Speaker < 0 || t >= l.T + l.Len) continue;
            if (l.Item["mp3"].Str() is { Length: > 0 } mp3) StartLip(_actors.List[l.Speaker], l.T, mp3);
        }
        foreach (var A in _actors.List)
        {
            var clip = SeqActors.AnimItemAt(A.A, t)?["anim"].Str();
            double mute = SceneSpeech.Mute(_lines, _index.GetValueOrDefault(A, -1), clip, t);
            if (!_lips.TryGetValue(A, out var lip))
            {
                if (mute <= 0) continue;
                _lips[A] = lip = MakeJaw(A);
            }
            lip?.Apply(t, mute);
        }
    }

    // ------------------------------------------------------------------------------------------- lip sync
    /// <summary>The jaw of one character: its skeleton and bone, its rest turn (the closed mouth), faceanim's frame 1
    /// turn and the lip sync's weight layers (none for an actor that only plays a talk gesture).</summary>
    sealed class Lip
    {
        public Skeleton3D Sk = null!;
        public int Bone;
        public Quaternion Turn, Rest;
        public List<List<(double T, double W)>> Layers = new();
        Quaternion? _base, _out;
        public double W, Mute;

        /// <summary>after the body clip: the jaw held towards its rest by mute (a talk gesture of an actor who does not
        /// speak: SceneSpeech.Mute), then turned by the layers' weights. Both go on top of what the body clip set this
        /// frame; a clip without a key for the jaw leaves last frame's turned pose, so that is put back first (turning it
        /// again every frame spun the jaw into the head - cutscene.js jaw).</summary>
        public void Apply(double t, double mute)
        {
            if (!GodotObject.IsInstanceValid(Sk)) return;
            W = Layers.Count > 0 ? LipSync.Weight(Layers, t) : 0;
            Mute = mute;
            var q = Sk.GetBonePoseRotation(Bone);
            if (_out is { } o && q.IsEqualApprox(o) && _base is { } b) q = b;
            _base = q;
            if (mute > 0) q = q.Normalized().Slerp(Rest, (float)Math.Min(1, mute)).Normalized();
            if (W > 0) q = (q * Quaternion.Identity.Slerp(Turn, (float)Math.Min(1, W))).Normalized();
            _out = q;
            Sk.SetBonePoseRotation(Bone, q);
        }

        /// <summary>the jaw's turn now and its rest (tests)</summary>
        public string Describe() => GodotObject.IsInstanceValid(Sk) ? $"pose {Sk.GetBonePoseRotation(Bone)} rest {Sk.GetBoneRest(Bone).Basis.GetRotationQuaternion()}" : "-";

        /// <summary>the scene is over: the jaw back where the body clip has it</summary>
        public void Restore()
        {
            if (!GodotObject.IsInstanceValid(Sk) || _base is not { } b || _out is not { } o) return;
            if (Sk.GetBonePoseRotation(Bone).IsEqualApprox(o)) Sk.SetBonePoseRotation(Bone, b);
        }
    }

    /// <summary>the jaw of an actor's model (null: no character's skeleton with a jaw)</summary>
    Lip? MakeJaw(SeqActors.Actor A)
    {
        var m = A.E != null ? S.Entities.ModelOf(A.E) : _temp.TryGetValue(A, out var tv) ? tv.M : null;
        if (m?.Template?.Clips is not { } clips) return null;
        var sk = m.Model.FindChild("Skeleton3D", true, false) as Skeleton3D;
        if (sk == null) return null;
        // the jaw: the bone of faceanim's first turn track with two keys (cutscene.js lip); its frame 1, or the shipped
        // clip's frame 1 when the converted keys are not usable (LipSync.Frame1)
        int bone = -1; Quaternion turn = default; bool ok = false;
        if (m.Template.ClipNames.TryGetValue("faceanim", out var cn) && clips.GetAnimation(cn) is Animation face)
            for (int i = 0; i < face.GetTrackCount() && bone < 0; i++)
            {
                if (face.TrackGetType(i) != Animation.TrackType.Rotation3D || face.TrackGetKeyCount(i) < 2) continue;
                bone = sk.FindBone(PwRemake.View.Anim.ClipTools.TrackTarget(face.TrackGetPath(i)));
                if (bone < 0) continue;
                var q0 = (Quaternion)face.TrackGetKeyValue(i, 0); var q1 = (Quaternion)face.TrackGetKeyValue(i, 1);
                if (LipSync.UsableTurn(new double[] { q0.X, q0.Y, q0.Z, q0.W }, new double[] { q1.X, q1.Y, q1.Z, q1.W })) { turn = (q0.Inverse() * q1).Normalized(); ok = true; }
            }
        if (bone < 0) bone = sk.FindBone(LipSync.JawBone);
        if (bone < 0) return null;
        if (!ok) { var f = LipSync.Frame1; turn = new Quaternion((float)f[0], (float)f[1], (float)f[2], (float)f[3]).Normalized(); }
        // (the rest pose of the jaw is the closed mouth: bela_s0 / cole_s0 idle_1..6 hold it there)
        return new Lip { Sk = sk, Bone = bone, Turn = turn, Rest = sk.GetBoneRest(bone).Basis.GetRotationQuaternion().Normalized() };
    }

    void StartLip(SeqActors.Actor A, double start, string mp3)
    {
        if (!_lips.TryGetValue(A, out var jaw)) _lips[A] = jaw = MakeJaw(A);
        if (jaw == null) return;
        // the .lsd next to the speech file (the installation's Audio/SeqSounds)
        var path = S.Assets.Resolve("assets/snd/../SeqSounds/" + System.Text.RegularExpressions.Regex.Replace(mp3, @"\.mp3$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase) + ".lsd");
        if (path == null) { Log.For("UI.Mission").WarnOnce("lsd:" + mp3, $"no lip-sync file for {mp3}"); return; }
        List<LipSync.Record>? recs = null;
        try { recs = LipSync.ReadLsd(System.IO.File.ReadAllBytes(path)); } catch (Exception e) { Log.For("UI.Mission").WarnOnce("lsd:" + mp3, $"lip-sync file {path}: {e.Message}"); }
        if (recs == null) return;
        var layers = LipSync.Layers(recs, start);
        if (layers.Count == 0) return;
        jaw.Layers = layers;
    }

    /// <summary>the jaw's weight of an actor's lip sync now and how far a talk gesture's jaw is held still (tests: --cine)</summary>
    public IEnumerable<(string Cls, double W, double Mute, string Jaw)> LipWeights() =>
        _lips.Where(kv => kv.Value != null).Select(kv => (kv.Key.A["cls"].Str(), kv.Value!.W, kv.Value.Mute, kv.Value.Describe()));

    // ------------------------------------------------------------------------------------------- the camera
    static double Lerp(double a, double b, double f) => a + (b - a) * f;
    static double[] Arr(JsonNode? n) => n is JsonArray a ? a.Select(v => v.Num()).ToArray() : Array.Empty<double>();

    // Keys: position [t, x, y, z, interp, tcb0, tcb1, ease0, ease1], turn [t, qx, qy, qz, qw, interp, ...], field of
    // view [t, fov, interp, ...]. Before the first key its value, after the last the last one's; segment i (key i-1 to
    // key i) uses key i's interpolation.
    static (int I, double S) Segment(List<double[]> K, double t)
    {
        if (t <= K[0][0]) return (0, 0);
        int L = K.Count;
        if (t > K[L - 1][0]) return (L - 1, 1);
        int i = 1;
        while (K[i][0] < t) i++;
        return (i, (t - K[i - 1][0]) / Math.Max(1e-6, K[i][0] - K[i - 1][0]));
    }
    static double Hermite(double p0, double p1, double t0, double t1, double s) { double s2 = s * s, s3 = s2 * s; return p0 * (2 * s3 - 3 * s2 + 1) + p1 * (3 * s2 - 2 * s3) + t0 * (s3 - 2 * s2 + s) + t1 * (s3 - s2); }

    static double[] TrackValue(List<double[]> K, double t, Func<int, double[]> vals, int o, bool eight)
    {
        var (i, s) = Segment(K, t);
        if (i == 0 || s >= 1) return vals(i);
        int L = K.Count; var k = K[i]; int type = (int)k[o];
        var P0 = vals(i - 1); var P1 = vals(i); var Pm = vals(Math.Max(i - 2, 0)); var P2 = vals(Math.Min(i + 1, L - 1));
        if (type is 1 or 2 or 3)
        {
            double w = type == 1 ? s : type == 2 ? (1 - Math.Cos(Math.PI * s)) / 2 : 3 * s * s - 2 * s * s * s;
            return P0.Select((v, j) => Lerp(v, P1[j], w)).ToArray();
        }
        if (type is 4 or 5)
        {
            double c = type == 4 ? 0.5 : k[o + 1];
            return P0.Select((v, j) => Hermite(v, P1[j], c * (P1[j] - Pm[j]), c * (P2[j] - v), s)).ToArray();
        }
        if (type == 8 && eight)
        {
            var kp = K[i - 1]; double c = k[o + 1], d = k[o + 3];
            double a = kp[o] == 8 ? kp[o + 2] : 0.5, b = kp[o] == 8 ? kp[o + 4] : 1;
            return P0.Select((v, j) => Hermite(v, P1[j], a * ((P1[j] - v) + b * (v - Pm[j])), c * ((P1[j] - v) + d * (P2[j] - P1[j])), s)).ToArray();
        }
        return P1;
    }

    // the forward (local +y) and up (local +z) columns of the rotation of q = [x, y, z, w] (not normalized)
    static double[] FwdOf(double[] q) => new[] { 2 * (q[0] * q[1] + q[2] * q[3]), 1 - 2 * (q[0] * q[0] + q[2] * q[2]), 2 * (q[1] * q[2] - q[3] * q[0]) };
    static double[] UpOf(double[] q) => new[] { 2 * (q[0] * q[2] - q[1] * q[3]), 2 * (q[1] * q[2] + q[0] * q[3]), 1 - 2 * (q[0] * q[0] + q[1] * q[1]) };
    static double[] Norm3(double[] v) { double n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); return n > 1e-12 ? v.Select(x => x / n).ToArray() : v; }
    static double[] Cross3(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    static double[] Quat(double[] k) => new[] { k[1], k[2], k[3], k[4] };

    // the camera's view direction in map space from its turn keys (the view's up is the world's, roll is dropped)
    static double[] ViewDir(List<double[]> K, double t)
    {
        var (i, s) = Segment(K, t);
        if (i == 0 || s >= 1) return FwdOf(Quat(K[i]));
        int L = K.Count; var k = K[i]; int type = (int)k[5];
        var q0 = Quat(K[i - 1]); var q1 = Quat(K[i]);
        if (type == 1)
        {
            double sg = q0[0] * q1[0] + q0[1] * q1[1] + q0[2] * q1[2] + q0[3] * q1[3] < 0 ? -1 : 1;
            return FwdOf(q0.Select((v, j) => (1 - s) * v + s * sg * q1[j]).ToArray());
        }
        if (type is 4 or 5)
        {
            double c = type == 4 ? 0.5 : k[6];
            var qm = Quat(K[Math.Max(i - 2, 0)]); var q2 = Quat(K[Math.Min(i + 1, L - 1)]);
            double[] H(Func<double[], double[]> f) { var m = f(qm); var a = f(q0); var b = f(q1); var n = f(q2); return a.Select((v, j) => Hermite(v, b[j], c * (b[j] - m[j]), c * (n[j] - v), s)).ToArray(); }
            var z = Norm3(H(UpOf)); var x = Norm3(Cross3(H(FwdOf), z));
            return Cross3(z, x);
        }
        if (type is 6 or 7)
        {
            // (SQUAD, type 7: no file uses it - slerped like 6)
            var a = new Quaternion((float)q0[0], (float)q0[1], (float)q0[2], (float)q0[3]).Normalized();
            var b = new Quaternion((float)q1[0], (float)q1[1], (float)q1[2], (float)q1[3]).Normalized();
            if (a.Dot(b) < 0) b = -b;
            var r = a.Slerp(b, (float)s);
            return FwdOf(new double[] { r.X, r.Y, r.Z, r.W });
        }
        return FwdOf(q1);
    }

    void Camera(SequenceTimeline P, double t)
    {
        var raw = P.Raw;
        if (raw?["cuts"] is not JsonArray cuts || raw["cams"] is not JsonObject cams) return;
        JsonArray? cut = null;
        foreach (var c in cuts) if (c is JsonArray ca && ca[0].Num() <= t) cut = ca;
        if (cut == null || cams[cut[2].Str()] is not JsonObject C) return;
        var pk = C["pk"] is JsonArray pka ? pka.Select(Arr).ToList() : new List<double[]>();
        var rk = C["rk"] is JsonArray rka ? rka.Select(Arr).ToList() : new List<double[]>();
        var fk = C["fk"] is JsonArray fka ? fka.Select(Arr).ToList() : new List<double[]>();
        var eye = pk.Count > 0 ? TrackValue(pk, t, i => pk[i][1..4], 4, true) : Arr(C["eye"]);
        var f = rk.Count > 0 ? ViewDir(rk, t) : FwdOf(Arr(C["q"]));
        double fov = fk.Count > 0 ? TrackValue(fk, t, i => new[] { fk[i][1] }, 2, false)[0] : C["fov"].Num(0.7);
        // the view camera: eye, target 10 m ahead, the world's up (map space x east, y south, z up)
        var E = (double[])eye.Clone(); var T = new[] { eye[0] + 10 * f[0], eye[1] + 10 * f[1], eye[2] + 10 * f[2] };
        // camera shakes (ItemCamFX): per axis of the mask (side, world up, view) amplitude x sin(time x frequency)
        foreach (var it in P.Items)
        {
            if (it.K != "shake" || it.Raw == null || it.Raw["mode"].Num() != 0 || t < it.T || t >= it.T + it.Len) continue;
            double tl = t - it.T, u = tl / Math.Max(1e-6, it.Len);
            int axes = (int)it.Raw["axes"].Num();
            if (it.Raw["shake"] is not JsonArray sh) continue;
            for (int k = 0; k < 3 && k < sh.Count; k++)
            {
                var s = Arr(sh[k]);
                if (((axes >> k) & 1) == 0 || s.Length < 3) continue;
                double v = s[0] * (s[2] != 0 ? 1 - u : 1) * Math.Sin(tl * s[1]);
                var D = new[] { T[0] - E[0], T[1] - E[1], T[2] - E[2] };
                var ax = Norm3(k == 0 ? new[] { D[1], -D[0], 0 } : k == 1 ? new double[] { 0, 0, 1 } : D);
                for (int j = 0; j < 3; j++) { E[j] += v * ax[j]; T[j] += v * ax[j]; }
            }
        }
        var Cm = S.Mission!.Campaign;
        var (x, z) = Cm.ToGame(E[0], E[1]); var (tx, tz) = Cm.ToGame(T[0], T[1]);
        var cam = S.Renderer.Camera;
        var pos = new Vector3((float)x, (float)E[2], (float)z);
        var tgt = new Vector3((float)tx, (float)T[2], (float)tz);
        if ((tgt - pos).LengthSquared() < 1e-6f) return;
        cam.GlobalPosition = pos;
        cam.LookAt(tgt, Vector3.Up);
        cam.Fov = (float)(fov * 180 / Math.PI);
        // (the world's sound listener and the shadows follow the shot)
        var (gx, gz) = Cm.ToGame(E[0] + f[0] * 30, E[1] + f[1] * 30);
        S.Camera.Target = new Vector3((float)gx, (float)E[2], (float)gz);
    }
}
