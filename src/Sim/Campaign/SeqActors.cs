// The actors of an in-engine cutscene (the toolkit's docs/SEQUENCE_FORMAT.md §8-9; the data: world.json
// sequences[path].play): which world objects take part, where each one is at a moment of the timeline, and where they
// are left.
//
//   * Binding (client 0x52dc24): the actor's object by GUID (the map object of that GUID); else the first world
//     object of the binding's class (or its equivalent of the player's tribe), owner (-1 none, -2 any), level (-1
//     any) and name ('' any) that no other actor took; else the actor is temporary - drawn for the scene only, with
//     the attachments of its state (weapons, shields: the view's cutscene player).
//   * Pose at time t: its start pose, then every walk / recorded path / beam that began by t (a walk or path between
//     its keys; heading from the path's turn keys on their own times, RotAt, else from the direction of the walk turning
//     from leg to leg, WalkHeading); a turn adds its angle. A walk is
//     straight from waypoint to waypoint (the server's walk of a cutscene does no path finding, PWServer 0x464ed0 ->
//     0x4a3180) at the times the editor stored, which are the server's own result for those waypoints (the client
//     re-times the waypoints from it, 0x51d600); with its flag bit 1 (most walks) on the terrain, else at the
//     waypoints' heights (0x489fc0).
//   * End (0x501820): without snap_actors_back every bound actor stays where the scene left it (the server sets
//     position and turn, stops its walk); with it everybody goes back to where it was before. A bound building's clip
//     goes on from the scene's (FinishAnim; the scene's clips: SceneClips; ItemAnim frame ranges: FrameTime).
//   * A temporary actor's look (Look): its class (the player's tribe's equivalent in a swapped mission), the models on
//     its links with their clips, and the scene's start links between actors (LinkedTo).
// Positions are map metres (x east, y south, z up); poses here are the game's (x, z, heading).
//
// Ported from remake/src/game/campaign/sequence.js (pw-exporter 98c7703). The view's cutscene player reads Pose() and
// Hidden(); Finish() is called by the mission at the sequence's end.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Campaign;

public sealed class SeqActors
{
    // m: a stored height this far off the terrain is meant (pose)
    const double OFF = 1.5, LIFT_MIN = 0.5;

    /// <summary>One actor of the scene: its data, the record and world object it is bound to (null: temporary) and its
    /// start pose.</summary>
    public sealed class Actor
    {
        public JsonNode A = null!;
        public Rec? Rec;
        public Entity? E;
        public Prop? Prop;
        public bool Temp;
        public (double X, double Z, double Y, double H, bool Visible)? Start;
        /// <summary>a temporary actor: the class it is drawn as - the player's own tribe's equivalent when the binding
        /// names the human player and the mission is played with another tribe (TribeSwap, as the world swaps the
        /// player's objects: Campaign.OwnClass)</summary>
        public string Cls = "";
        /// <summary>a temporary actor: the models on its links (weapons, riders, build-ups) with their clips</summary>
        public readonly List<ActorPart> Parts = new();
        /// <summary>the scene's start link (the actor block's embedded ItemLink, export "links"): this actor hangs at
        /// LinkSlot of that one (mission 2's transport turtle shell on the turtle; riders, gunners, a catapult)</summary>
        public Actor? LinkedTo;
        public string? LinkSlot;
        internal readonly Dictionary<JsonNode, double[]> Lifts = new();
    }
    /// <summary>A model on a link of a temporary actor: the link (slot), the model, the clip it plays (a rider's
    /// ride_idle_0), the part it hangs on (an index into Parts; -1: the actor's own model).</summary>
    public readonly record struct ActorPart(string Slot, string Gfx, string? Clip, int Parent);

    /// <summary>A pose at a moment: game x, z; map height mz; on the terrain or at mz; heading; the walk it is in and its
    /// speed there (m/s: the walk clip's time scale).</summary>
    public readonly record struct Pose(double X, double Z, double Mz, bool Ground, double H, JsonNode? Walk, double V = 0);

    readonly Campaign C;
    public readonly JsonNode Play;
    public readonly List<Actor> List = new();
    /// <summary>the cutscene player ran the timeline and moved the actors (snap back only then); set by the mission UI's
    /// end of the scene (SceneEndCommand.Timeline)</summary>
    public bool Moved;

    public SeqActors(Campaign c, JsonNode play) { C = c; Play = play; Bind(); }

    static string Lc(string? s) => (s ?? "").ToLowerInvariant();

    void Bind()
    {
        var taken = new HashSet<Rec>();
        var recs = C.Objects.All.Where(r => r.Guid != null && r.Alive).ToList();
        static Entity? ObjOf(Rec? r) => r?.Entity is { Alive: true } e ? e : null;
        static bool Live(Rec? r) => r != null && (ObjOf(r) != null || r.Prop != null);
        foreach (var a in Play["actors"]?.AsArray() ?? new JsonArray())
        {
            if (a == null) continue;
            var rec = C.Objects.ByGuid(a["guid"].Str());
            if (!Live(rec) || taken.Contains(rec!)) rec = null;
            if (rec == null)
            {
                var b = a["bind"] ?? new JsonObject();
                int bOwner = b["owner"] is JsonValue ? b["owner"].Int() : -1;
                var owner = bOwner >= 0 ? C.PlayerAt(bOwner) : null;
                // the class, or its equivalent of the player's tribe: an object given to the player in a mission played with
                // another tribe became that tribe's (Campaign.Convert) - mission 1's freed spearmen in sc_1040
                var bc = b["cls"].Str() is { Length: > 0 } x ? x : a["cls"].Str();
                string cls = Lc(bc), own = Lc(C.OwnClass(bc));
                int lvl = b["level"] is JsonValue ? b["level"].Int() : -1;
                var nm = b["name"].Str();
                // the owner: in a co-op game "the player" (seat 1's slot) is every seat, as for the triggers' queries
                // (Registry.Select, CoopRuntime.OwnerMatches) - the freed spearmen dealt to the seats are mission 1's
                // spearmen of sc_1040 all the same, and the scene's end puts them on Stina's side of the rockfall
                bool OwnerOk(Entity e) => e.Owner == owner || (C.Coop != null && e.Owner != null && bOwner >= 0 && C.Coop.OwnerMatches(e.Owner.Id, bOwner));
                rec = recs.FirstOrDefault(r => !taken.Contains(r) && Live(r) && (Lc(r.Cls) == cls || Lc(r.Cls) == own || C.Objects.ClassIs(r, cls)) &&
                    (bOwner == -2 || (ObjOf(r) != null ? OwnerOk(ObjOf(r)!) : bOwner < 0)) &&
                    (!(lvl >= 0) || ObjOf(r) == null || ((ObjOf(r) as Unit)?.Level ?? 1) - 1 == lvl) &&
                    (nm.Length == 0 || Lc(r.Name) == Lc(nm)));
            }
            if (rec != null) taken.Add(rec);
            var e = ObjOf(rec); var prop = e == null ? rec?.Prop : null;
            var act = new Actor { A = a, Rec = rec, E = e, Prop = prop, Temp = e == null && prop == null };
            if (e != null) act.Start = (e.Pos.X, e.Pos.Z, e.Pos.Y, e is Building bb ? bb.Rot : e.Heading, !e.Parked);
            else if (prop != null) act.Start = (prop.Pos.X, prop.Pos.Z, prop.Pos.Y, prop.Rot, prop.Visible);
            List.Add(act);
        }
        foreach (var A in List) if (A.Temp) Look(A);
        // the start links between actors (op 2 = attached; the exporter writes them as [slot, guid])
        var byGuid = new Dictionary<string, Actor>(StringComparer.Ordinal);
        foreach (var A in List) byGuid.TryAdd(A.A["guid"].Str(), A);
        foreach (var A in List)
            foreach (var l in A.A["links"]?.AsArray() ?? new JsonArray())
                if (l is JsonArray la && la.Count >= 2 && byGuid.TryGetValue(la[1].Str(), out var ch) && ch != A && ch.LinkedTo == null)
                { ch.LinkedTo = A; ch.LinkSlot = la[0].Str(); }
    }

    /// <summary>
    /// How a temporary actor looks: its class (the player's tribe's equivalent for an actor of the human player in a
    /// swapped mission) and the models on its links. Unswapped: the scene's own attachments (the actor state's, docs/
    /// SEQUENCE_FORMAT.md §8), each with its clip - the state's (export: a third element; "ride_idle_0" for the riders,
    /// "weapon_off" for the flamers), else the composite table's for that link and model, else a rider's "ride_idle_0"
    /// on "Ride" (the rider stood upright on mission 2's ankylosaurus). Swapped: the swapped class's own parts - its
    /// weapon (characters), the composite parts it shows without upgrades (riders, the transport turtle's shell) - as
    /// the attachments of the scene belong to the class it was made with (mission 2's Norse ship with spray and helmsman).
    /// </summary>
    void Look(Actor A)
    {
        var a = A.A;
        var cls = a["cls"].Str();
        A.Cls = cls;
        var D = C.World.Data;
        var b = a["bind"] ?? new JsonObject();
        int bOwner = b["owner"] is JsonValue ? b["owner"].Int() : -1;
        var owner = bOwner >= 0 ? C.PlayerAt(bOwner) : null;
        if (owner != null && owner == C.Human && C.Swap != null)
        {
            var own = C.OwnClass(cls);
            if (!string.Equals(own, cls, StringComparison.OrdinalIgnoreCase) && C.World.UnitModel(own, 1, owner) != null)
            {
                A.Cls = own;
                SwappedParts(A, own, owner);
                return;
            }
        }
        var specs = D.Composites(cls);
        foreach (var p in a["attach"]?.AsArray() ?? new JsonArray())
        {
            if (p is not JsonArray pa || pa.Count < 2 || pa[0] is not JsonValue sv || !sv.TryGetValue(out string? slot) || string.IsNullOrEmpty(slot)) continue;
            var gfx = pa[1].Str();
            if (gfx.Length == 0) continue;
            string? clip = pa.Count > 2 && pa[2].Str() is { Length: > 0 } c ? c : null;
            clip ??= RestClip(specs.FirstOrDefault(s => string.Equals(s.Link, slot, StringComparison.OrdinalIgnoreCase) && RestClip(s) != null &&
                (string.Equals(s.Gfx, gfx, StringComparison.OrdinalIgnoreCase) || s.Variants.Any(v => string.Equals(v, gfx, StringComparison.OrdinalIgnoreCase)))));
            clip ??= specs.FirstOrDefault(s => s.Kind == "rider" && string.Equals(s.Link, slot, StringComparison.OrdinalIgnoreCase) && s.Anim != null)?.Anim;
            if (clip == null && slot == "Ride") clip = "ride_idle_0";
            A.Parts.Add(new ActorPart(slot, gfx.ToLowerInvariant(), clip, -1));
        }
    }

    // the parts of a swapped class: its weapon in hand, and the composite parts shown without any upgrade (Compose.ActiveParts)
    void SwappedParts(Actor A, string cls, Player owner)
    {
        var W = C.World; var D = W.Data;
        if (D.WeaponSetOf(cls, 1, owner).Long is { } w)
            foreach (var p in w.Parts)
                if (p.Gfx.Count > 0 && W.Models.Has(p.Gfx[0])) A.Parts.Add(new ActorPart(string.IsNullOrEmpty(p.Link) ? "HndR" : p.Link, p.Gfx[0].ToLowerInvariant(), null, -1));
        var specs = D.Composites(cls);
        var on = new HashSet<int>();
        foreach (var s in specs) if (s.Kind == "buildup" && s.Cond is "always" or "ready") on.Add(s.I);
        var index = new Dictionary<int, int>();
        foreach (var s in specs)
        {
            bool yes = s.Kind == "buildup" ? on.Contains(s.I)
                : s.Cond == "buildup" ? (s.ArgList ?? new List<int>()).Any(on.Contains)
                : s.Cond == "unless" ? !(s.ArgList ?? new List<int>()).Any(on.Contains)
                : s.Cond is "always" or "ready";
            int parent = -1;
            if (yes && s.Pi >= 0) { if (index.TryGetValue(s.Pi, out var pi)) parent = pi; else yes = false; }
            var gfx = s.Variants.Count > 0 ? s.Variants[0] : s.Gfx;
            if (!yes || gfx.Length == 0 || !W.Models.Has(gfx)) continue;
            index[s.I] = A.Parts.Count;
            A.Parts.Add(new ActorPart(s.Link, gfx.ToLowerInvariant(), RestClip(s), parent));
        }
    }

    /// <summary>
    /// The clip a composite part plays at rest in a scene (null: none - the part keeps its model's pose). The table's
    /// clip of a turret is the one it shoots with (seas_wehrspinne_top "attack_front", the only clip of that model; the
    /// world's view plays it only while the unit fights, Composite.Build) - played as the part's loop the Black Widows
    /// of mission 13's sc_4070 / sc_4080, and the SEAS Black Widow the player's allosaurus becomes in a swapped game,
    /// fired for the whole scene. So: no turret's clip and no attack clip; riders keep theirs (ride_idle_0,
    /// balista_stand). The scene's own attachment clips (the state's, export: a third element) are the original's and
    /// stay as they are.
    /// </summary>
    public static string? RestClip(CompositeSpec? s) =>
        s == null || s.Kind == "turret" || s.Anim == null || s.Anim.StartsWith("attack", StringComparison.OrdinalIgnoreCase) ? null : s.Anim;

    /// <summary>the model an actor drawn for the scene only is drawn with: its class's (World.UnitModel: rules, class
    /// file, stand-ins), else the model of the class's name - an object only the cutscenes use, which the official game's
    /// class files do not even name (mission 1's NS1050_stina_eusmilus, Stina with her sabre-tooth in sc_1050; its model
    /// comes from tools/prepare/cutscene_models.py); null: none</summary>
    public static string? ModelOf(PwRemake.Sim.World.World w, string cls)
    {
        if (string.IsNullOrEmpty(cls)) return null;
        var m = w.UnitModel(cls, 1, null);
        if (m != null) return m;
        var lc = cls.ToLowerInvariant();
        return w.Models.Has(lc) ? lc : null;
    }

    double Height(double x, double z) => C.World.Height(x, z);
    static double[] Arr(JsonNode? n) => n is JsonArray a ? a.Select(v => v.Num()).ToArray() : Array.Empty<double>();

    /// <summary>the pose of an actor at time t</summary>
    public Pose PoseAt(Actor A, double t)
    {
        var a = A.A;
        var pos = Arr(a["pos"]);
        var (x, z) = C.ToGame(pos.Length > 0 ? pos[0] : 0, pos.Length > 1 ? pos[1] : 0);
        double mz = pos.Length > 2 ? pos[2] : 0, h = Frame.MapHeading(a["rot"].Num());
        JsonNode? walk = null; bool ground = true; double v = 0;
        // the heights the file records are the original's (§9.3: an actor is set to its stored start pose; a VisPath is a
        // recorded motion; a walk's waypoints are where the engine's walk went - over walkable objects too). Snapping them
        // all to the terrain put mission 7's heroes waist-deep into the water sanctuary's spire in sc_2567 (and the spire,
        // stored 16 m below the terrain to rise out of it, on top of them). So: a stored height clearly off the terrain
        // (OFF m) is kept; a walk keeps its waypoints' height above the terrain, interpolated between them.
        double Off(double hx, double hz, double y) => y - Height(hx, hz);
        if (Math.Abs(Off(x, z, mz)) > OFF) ground = false;
        foreach (var it in a["items"]?.AsArray() ?? new JsonArray())
        {
            if (it == null || it["t"].Num() > t) continue;
            var k = it["k"].Str(); double it0 = it["t"].Num();
            var K = it["keys"] as JsonArray;
            if ((k == "walk" || k == "path") && K != null && K.Count > 0)
            {
                var keys = K.Select(Arr).ToList();
                double px = x, pz = z;
                // the walk begins where the actor stands (not after a cut to another place): it turns into the walk
                bool Near(double[] k0) { var (gx, gz) = C.ToGame(k0[1], k0[2]); return (gx - px) * (gx - px) + (gz - pz) * (gz - pz) < 4; }
                double lt = Math.Min(t - it0, keys[^1][0]);
                int i = 0;
                while (i < keys.Count - 2 && keys[i + 1][0] < lt) i++;
                var k0 = keys[i]; var k1 = keys[Math.Min(i + 1, keys.Count - 1)];
                double f = k1[0] > k0[0] ? Math.Max(0, Math.Min(1, (lt - k0[0]) / (k1[0] - k0[0]))) : 1;
                double mx = k0[1] + (k1[1] - k0[1]) * f, my = k0[2] + (k1[2] - k0[2]) * f;
                mz = k0[3] + (k1[3] - k0[3]) * f;
                ground = k != "walk" || !(it["ground"] is JsonValue gv && gv.TryGetValue(out bool gb) && !gb);
                (x, z) = C.ToGame(mx, my);
                if (ground)
                {
                    if (k == "path") ground = Math.Abs(Off(x, z, mz)) <= OFF;      // a recorded motion: its own heights
                    else
                    {                                                             // a walk: lifted where its waypoints are
                        var lift = Lift(A, it, keys); double l = lift[i] + (lift[Math.Min(i + 1, keys.Count - 1)] - lift[i]) * f;
                        if (l > 0.05) { ground = false; mz = Height(x, z) + l; }
                    }
                }
                // the heading: the recorded turn keys on their own times (a path's turn often outlasts its position keys:
                // sc_3080's Stina turns to the warden over 0.6 s on one position key - holding the turn at the last
                // position key's time left her facing away from whom she talks to in 188 turns of 35 scenes), else a
                // walk's direction, turning into each new one (WalkHeading)
                if (it["rots"] is JsonArray rots && rots.Count > 0) h = Frame.MapHeading(RotAt(rots.Select(Arr).ToList(), t - it0));
                else if (k == "walk") { if (WalkHeading(keys, t - it0, h, Near(keys[0])) is double wh) h = wh; }
                else if (!ReferenceEquals(k1, k0) && Math.Sqrt((k1[1] - k0[1]) * (k1[1] - k0[1]) + (k1[2] - k0[2]) * (k1[2] - k0[2])) > 0.05) h = Geo.HeadingTo(k1[1] - k0[1], k1[2] - k0[2]);
                if (k == "walk" && t - it0 < it["len"].Num() && f < 1)
                {
                    walk = it;
                    v = k1[0] > k0[0] ? Math.Sqrt((k1[1] - k0[1]) * (k1[1] - k0[1]) + (k1[2] - k0[2]) * (k1[2] - k0[2])) / (k1[0] - k0[0]) : 0;
                }
            }
            else if (k == "beam")
            {
                var p = Arr(it["pos"]);
                (x, z) = C.ToGame(p[0], p[1]); mz = p[2]; h = Frame.MapHeading(it["rot"].Num()); ground = Math.Abs(Off(x, z, mz)) <= OFF;
            }
            else if (k == "turn")
            {
                double len = it["len"].Num();
                h += it["angle"].Num() * (len > 0 ? Math.Min(1, (t - it0) / len) : 1);
            }
        }
        return new Pose(x, z, mz, ground, h, walk, v);
    }
    /// <summary>s: a walk turns into its first direction (from where the actor faced) and from one leg into the next
    /// over this time - the walk's legs are straight lines, and the heading of each snapped round at its waypoint</summary>
    public const double TURN = 0.3;
    static double LerpAngle(double a, double b, double f) => a + Math.IEEERemainder(b - a, 2 * Math.PI) * Math.Clamp(f, 0, 1);

    /// <summary>
    /// The map turn (rot, radians; Frame.MapHeading gives the heading) of a recorded path's turn keys [time, rot] at the
    /// item's local time lt: before the first key the first, after the last the last; between two keys the shorter way
    /// round (the original slerps the keys' quaternions, ItemVisPath) at an even pace. The keys' own times count - not
    /// the path's position keys'.
    /// </summary>
    public static double RotAt(IReadOnlyList<double[]> rots, double lt)
    {
        if (rots.Count == 0) return 0;
        if (lt <= rots[0][0]) return rots[0][1];
        for (int i = 1; i < rots.Count; i++)
        {
            var a = rots[i - 1]; var b = rots[i];
            if (lt > b[0]) continue;
            double f = b[0] > a[0] ? (lt - a[0]) / (b[0] - a[0]) : 1;
            return LerpAngle(a[1], b[1], f);
        }
        return rots[^1][1];
    }

    /// <summary>
    /// The heading of a walk (waypoint keys [time, x, y, z], map metres) at its local time lt: the direction of the leg
    /// it is on; within TURN/2 of a waypoint between two legs it turns from the one direction into the next; in its
    /// first TURN seconds it turns from hPrev (the heading before the walk) when turnIn. null: no leg is longer than 5 cm.
    /// </summary>
    public static double? WalkHeading(IReadOnlyList<double[]> keys, double lt, double hPrev, bool turnIn)
    {
        int n = keys.Count;
        if (n < 2) return null;
        var dir = new double?[n - 1];
        for (int j = 0; j < n - 1; j++)
        {
            double dx = keys[j + 1][1] - keys[j][1], dy = keys[j + 1][2] - keys[j][2];
            if (dx * dx + dy * dy > 0.0025) dir[j] = Geo.HeadingTo(dx, dy);
        }
        for (int j = 1; j < n - 1; j++) dir[j] ??= dir[j - 1];
        for (int j = n - 3; j >= 0; j--) dir[j] ??= dir[j + 1];
        if (dir[0] is null) return null;
        lt = Math.Clamp(lt, 0, keys[^1][0]);
        int i = 0;
        while (i < n - 2 && keys[i + 1][0] < lt) i++;
        double h = dir[i]!.Value;
        if (i > 0 && lt - keys[i][0] < TURN / 2) h = LerpAngle(dir[i - 1]!.Value, h, 0.5 + (lt - keys[i][0]) / TURN);
        else if (i + 1 < n - 1 && keys[i + 1][0] - lt < TURN / 2) h = LerpAngle(h, dir[i + 1]!.Value, 0.5 - (keys[i + 1][0] - lt) / TURN);
        if (turnIn && lt < TURN) h = LerpAngle(hPrev, h, lt / TURN);
        return h;
    }

    // per waypoint of a walk: how far above the terrain it is (0 within LIFT_MIN m: on the ground)
    double[] Lift(Actor A, JsonNode it, List<double[]> keys)
    {
        if (A.Lifts.TryGetValue(it, out var l)) return l;
        l = keys.Select(k => { var (x, z) = C.ToGame(k[1], k[2]); double d = k[3] - Height(x, z); return d > LIFT_MIN ? d : 0; }).ToArray();
        A.Lifts[it] = l;
        return l;
    }
    // ------------------------------------------------------------------------------------------- animation items
    /// <summary>the frames per second of the sequencer's animation items (docs/SEQUENCE_FORMAT.md §9.5)</summary>
    public const double FPS = 25;
    /// <summary>The animation item of an actor at time t: the last ItemAnim ("anim") / ItemAnimAct ("act") begun by t
    /// whose time has not run out; an ItemAnim with repeat 0 holds on until the next one.</summary>
    public static JsonNode? AnimItemAt(JsonNode actor, double t)
    {
        JsonNode? cur = null;
        foreach (var it in actor["items"]?.AsArray() ?? new JsonArray())
        {
            if (it == null) continue;
            var k = it["k"].Str(); double it0 = it["t"].Num(), len = it["len"].Num();
            if ((k == "act" || k == "anim") && it0 <= t && (t < it0 + Math.Max(len, 0.05) || (k == "anim" && it["rep"].Num(-1) == 0))) cur = it;
        }
        return cur;
    }
    /// <summary>
    /// The clip time of an ItemAnim at scene time t (SEQUENCE_FORMAT.md §8.1: frames first..last, inclusive, at 25 per
    /// second; `repeat` times - 0 once - then held on the last frame). Most items of buildings are frame ranges, not the
    /// whole clip: mission 16's end boss gates hold frame 0 of "close" (open) for 37 s, then close over frames 0..50;
    /// a held single frame (first = last) is a pose. clipDuration: the clip's length (the last frame's time); null for an
    /// item that is no ItemAnim.
    /// </summary>
    public static double? FrameTime(JsonNode it, double clipDuration, double t)
    {
        if (it["k"].Str() != "anim") return null;
        double end = Math.Max(0, clipDuration);
        double f0 = it["f0"].Num(), f1 = it["f1"].Num(-1);
        double t0 = Math.Clamp(f0 / FPS, 0, end), t1 = f1 < 0 ? end : Math.Clamp(f1 / FPS, t0, end);
        double span = t1 - t0, lt = Math.Max(0, t - it["t"].Num());
        if (span <= 1e-6) return t0;
        int n = Math.Max(1, (int)it["rep"].Num(1));
        if (lt >= span * n) return t1;
        return t0 + lt % span;
    }

    /// <summary>hidden at time t (the scene's attribute items); null = the scene says nothing</summary>
    public bool? Hidden(Actor A, double t)
    {
        bool? hid = null;
        foreach (var it in A.A["items"]?.AsArray() ?? new JsonArray())
            if (it != null && it["k"].Str() == "attr" && it["t"].Num() <= t && it["hidden"] is JsonValue hv) hid = hv.Bool();
        return hid;
    }
    /// <summary>the end: bound actors stay where the scene left them (or go back: snap_actors_back)</summary>
    /// <remarks>What the cutscene player (the view) did to the world while it played the timeline is done here, at the
    /// end, so that it is the same on every machine and in a replay (docs/SAVES.md): the JS player put the actors on
    /// their pose of the moment every frame, marked them busy and gave them their idle clip at its end; the last pose of
    /// an actor that does not move is its pose of the scene. Now the view poses them for drawing only and the world takes
    /// the scene's lasting result here: the idle clip, and the actors that do not move on the scene's pose.</remarks>
    public void Finish(bool snapBack)
    {
        double t = Play["end"].Num();
        // cutscene.js end: the actors free again, on their idle clip; the other bound objects (buildings: gates, the
        // Scorpion, garages) on the clip the scene left them with
        if (Moved)
            foreach (var A in List)
            {
                if (A.E is Unit u && u.Alive) { u.BusyAnim = false; if (u.Anim != null && u.IdleAnim != null) u.Anim.Play(u.IdleAnim); }
                else if (A.E is { Alive: true } && A.E is not Unit) FinishAnim(A, t);
                else if (A.E == null && A.Prop is { Alive: true }) FinishAnim(A, t);       // (a bound prop: sc_4070's carrier stays open)
            }
        foreach (var A in List)
        {
            if (A.Temp) continue;
            if (A.E == null && A.Prop == null) continue;
            if (A.E != null && !A.E.Alive) continue;
            if (snapBack) { if (Moved && A.Start is { } s) Place(A, s.X, s.Z, s.H, null); continue; }
            bool moves = (A.A["items"]?.AsArray() ?? new JsonArray()).Any(it => it?["k"].Str() is "walk" or "path" or "beam" or "turn");
            var p = PoseAt(A, t);
            if (moves) { Place(A, p.X, p.Z, p.H, p.Ground ? null : p.Mz); continue; }
            if (!Moved) continue;
            // an actor that does not move stays on the scene's pose (where the cutscene player put it every frame)
            double y = p.Ground ? Height(p.X, p.Z) : p.Mz;
            if (A.E is Unit su) { su.Pos = new V3(p.X, y, p.Z); su.Heading = p.H; }
            else if (A.Prop != null) { A.Prop.Pos = new V3(p.X, y, p.Z); A.Prop.Rot = p.H; A.Prop.LookVersion++; }
        }
    }
    /// <summary>
    /// A bound object that is no unit (a building) at the scene's end: the cutscene player ran its clip on the scene's
    /// time while the world stood still (SessionSequenceStage; the JS ran the world's own clip, cutscene.js apply); the
    /// world's clip goes on from what the scene showed - the last animation item's pose at the end (a gate the scene
    /// closed stays closed), else its own clip that much further (mission 16's open gates finish opening during
    /// sc_7050 instead of starting again after it). A clip with a callback (a behaviour waiting for its end) is left.
    /// </summary>
    void FinishAnim(Actor A, double end)
    {
        if ((A.E != null ? A.E.Anim : A.Prop?.Anim) is not { } an) return;
        JsonNode? last = null;
        foreach (var it in A.A["items"]?.AsArray() ?? new JsonArray())
            if (it?["k"].Str() is "anim" or "act" && it["t"].Num() <= end && an.Has(it["anim"].Str())) last = it;
        if (last == null)
        {
            if (an.Cur != null && an.OnceCb == null) an.Update(Math.Max(0, end - Play["start"].Num()));
            return;
        }
        if (an.OnceCb != null) return;
        var clip = last["anim"].Str();
        var a = an.Play(clip, new AnimOpts { Loop = false, Restart = true, Fade = 0 });
        if (a == null) return;
        double dur = an.Duration(clip);
        a.Time = FrameTime(last, dur, end) ?? dur;
        a.Paused = true;
    }

    // (a unit goes onto the ground: the game has no walkable objects; a prop keeps a height the scene gives it, y)
    void Place(Actor A, double x, double z, double h, double? y)
    {
        var W = C.World;
        if (A.E is Unit u) { W.Teleport(u, x, z, h); return; }
        if (A.Prop != null)
        {
            var P = A.Prop;
            double py = y ?? (A.Start is { } s && Math.Sqrt((s.X - x) * (s.X - x) + (s.Z - z) * (s.Z - z)) < 0.01 ? s.Y : W.Height(x, z));
            P.Pos = new V3(x, py, z); P.Rot = h; P.LookVersion++;
        }
    }
}
