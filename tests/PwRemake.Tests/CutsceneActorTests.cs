// How the cutscenes' actors drawn for the scene only look (SeqActors.Look; player report, mission 2 "Druid Island"
// played as the Dustriders: "wrong model was used for the turtle, dustriders have their own turtle; also the
// ankylosaur is missing its catapult" - a plain giant turtle among the Dustriders, a man standing on the ankylosaur).
//
// * The turtle: sc_1082's Macrolemys_Land_0 carries the transport turtle's shell (aje_transport_turtle_shell_1_tmp) on
//   "con" by the scene's start link (the actor block's embedded ItemLink), which the toolkit's export left out - now
//   written by the prepare tool (tools/prepare/seq_actors.py: "links") and hung there by the cutscene player.
// * The ankylosaurus: the original's actor carries a rider on "Ride" playing "ride_idle_0" (the state's attachment
//   clip, also left out by the export) and no catapult (an ankylosaurus without the catapult upgrade - the scene's own
//   attachments: Ride -> aje_rider_a); the rider stood upright in his default pose. Now the attachment's clip (the
//   export's third element, else the composite table's, else ride_idle_0 on Ride).
// * Swapped tribes: an actor of the human player that the world does not have is drawn as the player's tribe's
//   equivalent (Campaign.OwnClass), with that class's own weapon / parts - mission 2's Norse transport ship in sc_1089
//   is the Dustriders' transport turtle (with its shell) when played as the Dustriders.
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

namespace PwRemake.Tests;

[TestClass]
public static class CutsceneActorTests
{
    static JsonNode Play(MissionSession s, string seq) => s.Campaign.Data.Sequences.First(kv => kv.Key.EndsWith(seq, StringComparison.OrdinalIgnoreCase)).Value!["play"]!.DeepClone();
    static SeqActors.Actor Named(SeqActors a, string name) => a.List.First(x => x.A["name"].Str() == name);

    public static void Mission2TurtleAndAnkylosaurus(TestContext t)
    {
        foreach (var tribe in new[] { "Aje", "" })
        {
            var s = CampaignTests.Session(t, "base", 2, tribe);
            var play = Play(s, "sc_1082.seq");
            // the start link as tools/prepare/seq_actors.py exports it (the container's test content predates it)
            var mac = play["actors"]!.AsArray().First(a => a!["name"].Str() == "Macrolemys_Land_0")!;
            if (mac["links"] == null) mac["links"] = new JsonArray { new JsonArray { "con", "dklgdgkhkidkknbbolckaabbjaoiboeo" } };
            var A = new SeqActors(s.Campaign, play);
            var shell = Named(A, "aje_transport_turtle_shell_1_tmp");
            t.True(shell.Temp && shell.LinkedTo == Named(A, "Macrolemys_Land_0") && shell.LinkSlot == "con", $"{tribe}: the shell hangs on the turtle at con");
            var anky = Named(A, "aje_ankylosaurus_11");
            t.True(anky.Temp && anky.Cls == "aje_ankylosaurus", $"{tribe}: the ankylosaurus is drawn for the scene (not the player's: no swap)");
            t.True(anky.Parts.Count == 1 && anky.Parts[0] == new SeqActors.ActorPart("Ride", "aje_rider_a", "ride_idle_0", -1), $"{tribe}: its rider rides (Ride, ride_idle_0): {string.Join(", ", anky.Parts)}");
            // with the export's own clip
            var anky2 = play["actors"]!.AsArray().First(a => a!["name"].Str() == "aje_ankylosaurus_11")!;
            anky2["attach"] = new JsonArray { new JsonArray { "Ride", "aje_rider_a", "ride_idle_1" }, new JsonArray { 0, "dummy" } };
            t.Equal("ride_idle_1", Named(new SeqActors(s.Campaign, play), "aje_ankylosaurus_11").Parts[0].Clip, $"{tribe}: the exported clip first");
            // sc_1089: the player's transport ship
            var B = new SeqActors(s.Campaign, Play(s, "sc_1089.seq"));
            var ship = Named(B, "hu_transport_ship_1");
            if (tribe == "Aje")
            {
                t.True(ship.Temp && ship.Cls == "aje_transport_turtle", $"as the Dustriders the player's ship is their transport turtle ({ship.Cls})");
                t.True(ship.Parts.Any(p => p.Slot == "con" && p.Gfx == "aje_transport_turtle") || !s.World.Models.Has("aje_transport_turtle"), "with its shell: " + string.Join(", ", ship.Parts));
                t.True(!ship.Parts.Any(p => p.Gfx.StartsWith("hu_") || p.Gfx.StartsWith("spray")), "none of the ship's parts");
                var arch = Named(B, "hu_archer_0");
                t.True(arch.E != null || arch.Cls == "aje_archer", $"the player's archer: bound or a Dustrider archer ({arch.Cls})");
            }
            else t.True(ship.Cls == "hu_transport_ship" && ship.Parts.Any(p => p.Gfx == "hu_rider_a" || p.Slot == "SpBa"), "as the Norsemen: the ship as the scene has it");
        }
    }

    /// <summary>
    /// Every mission's cutscenes (base configuration; MIRAGE's are the same scenes) with every tribe: an actor of the
    /// human player drawn for the scene is the class the world would make of it (no Norse warriors among a Dustrider
    /// army), with no part of the class it was made with; every rider on "Ride" plays a clip.
    /// </summary>
    public static void SwappedActorsEveryMission(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no base configuration");
        var bad = new List<string>(); int swapped = 0, riders = 0, n = 0;
        foreach (var e in MissionEntry.LoadIndex(c!.Config.MissionsDir))
        {
            if (WorldBuilder.FindMap(c.Config, e.Map) == null) continue;
            // only missions with an actor of the player (slot 0) of a tribe's class the world does not have
            var world = PwRemake.Sim.Maps.PwMapReader.WorldOf(Path.Combine(c.Config.MissionsDir, e.Folder));
            if (world == null) continue;
            var guids = world["objects"]!.AsArray().Select(o => o!["guid"].Str()).ToHashSet();
            bool any = world["sequences"]!.AsObject().Any(kv => (kv.Value?["play"]?["actors"]?.AsArray() ?? new JsonArray()).Any(a =>
                a!["bind"]?["owner"].Int(-1) == 0 && !guids.Contains(a["guid"].Str()) && TribeSwap.TribeOfClass(a["cls"].Str()) != null));
            if (!any) continue;
            foreach (var tribe in TribeSwap.TRIBES)
            {
                MissionSession s;
                try { s = CampaignTests.Session(t, "base", e.Id, tribe); } catch (SkipException) { continue; }
                n++;
                var C = s.Campaign;
                foreach (var (path, sq) in C.Data.Sequences)
                {
                    if (sq?["play"] is not JsonObject pj) continue;
                    foreach (var A in new SeqActors(C, pj).List.Where(x => x.Temp))
                    {
                        var cls = A.A["cls"].Str();
                        int owner = A.A["bind"]?["owner"].Int(-1) ?? -1;
                        if (owner >= 0 && C.PlayerAt(owner) == C.Human && C.Swap != null)
                        {
                            var own = C.OwnClass(cls);
                            bool can = s.World.UnitModel(own, 1, C.Human) != null;
                            if (!string.Equals(own, cls, StringComparison.OrdinalIgnoreCase) && can)
                            {
                                swapped++;
                                if (A.Cls != own) bad.Add($"{e.Id} as {tribe} {TrigUtil.FilePart(path)}: {A.A["name"].Str()} drawn as {A.Cls}, the world's {own}");
                                var foreign = TribeSwap.TribeOfClass(cls);
                                if (A.Parts.Any(p => foreign != null && TribeSwap.TribeOfClass(p.Gfx) == foreign && TribeSwap.TribeOfClass(own) != foreign))
                                    bad.Add($"{e.Id} as {tribe} {TrigUtil.FilePart(path)}: {A.A["name"].Str()} ({A.Cls}) keeps parts of {cls}: {string.Join(", ", A.Parts)}");
                            }
                        }
                        foreach (var p in A.Parts.Where(p => p.Slot == "Ride")) { riders++; if (p.Clip == null) bad.Add($"{e.Id} {TrigUtil.FilePart(path)}: the rider of {A.A["name"].Str()} has no clip"); }
                    }
                }
            }
        }
        foreach (var b in bad.Take(40)) t.Log("  " + b);
        t.Log($"{n} sessions, {swapped} actors of the player drawn as their tribe's equivalent, {riders} riders");
        if (n == 0) t.Skip("no missions");
        t.True(bad.Count == 0, $"{bad.Count} actors drawn wrongly");
        t.True(swapped > 0, "actors swapped");
    }

    /// <summary>
    /// Mission 1's rockfall (player report: "in the first mission when the boulder splits forces allied pike man were
    /// left stuck with Cole instead of with Stina"). In sc_1040 the four freed spearmen (bound by their names
    /// hu_spearman_14..17, owner 0) run past the falling rocks to Stina's side and the scene, played without
    /// snap_actors_back, leaves them there (SeqActors.Finish); Cole stays behind the rocks. In a co-op game the freed
    /// spearmen are dealt to the seats (other owners than slot 0): the binding found only seat 1's and the others stayed
    /// with Cole - now "the player" of a binding is every seat, as for the triggers (CoopRuntime.OwnerMatches), and a
    /// seat of another tribe has its own equivalent (Registry.ClassIs). Single player as the Norsemen and the
    /// Dustriders, co-op with two AI commanders and with three tribes; and sc_1050 binds the spearmen of every seat.
    /// </summary>
    public static void Mission1RockfallSplit(TestContext t)
    {
        var runs = new (string Name, Func<MissionSession> Make)[]
        {
            ("Hu", () => CampaignTests.Session(t, "base", 1, "")),
            ("Aje", () => CampaignTests.Session(t, "base", 1, "Aje")),
            ("co-op human,ai,ai", () => CoopTests.Session(t, CoopTests.CoopStart(t, "base", 1, new[] { ("human", ""), ("ai", ""), ("ai", "") }))),
            ("co-op Hu,Aje,Ninigi", () => CoopTests.Session(t, CoopTests.CoopStart(t, "base", 1, new[] { ("human", ""), ("human", "Aje"), ("human", "Ninigi") }))),
        };
        foreach (var (name, make) in runs)
        {
            var s = make(); var C = s.Campaign; var W = s.World;
            string[] names = { "hu_spearman_14", "hu_spearman_15", "hu_spearman_16", "hu_spearman_17" };
            var recs = names.Select(n => C.Objects.All.FirstOrDefault(r => r.Name == n && r.Alive)).ToList();
            t.True(recs.All(r => r != null && r.Entity?.Owner?.Id == 3), $"{name}: the four captured spearmen (player 3)");
            // freed: player_0_in_ready's OCPY gives them to "the player" (dealt to the seats in a co-op game)
            s.Host.GiftBatch(true);
            foreach (var r in recs) s.Host.SetOwner(r!.Id, 0, 5);
            s.Host.GiftBatch(false);
            var owners = recs.Select(r => r!.Entity!.Owner!.Id).ToList();
            if (C.Coop != null) t.True(owners.Distinct().Count() > 1, $"{name}: the spearmen are dealt to several seats ({string.Join(" ", owners)})");
            else t.True(owners.All(o => o == 0), $"{name}: the spearmen are the player's");
            var cole = W.Units.First(u => u.Alive && u.Name == "Cole_s0");
            var r1040 = new SequenceRequest { Path = "Cpn_single_001/single_01/sc_1040.seq", Trigger = "S02#sc_1040" };
            s.Host.PlaySequence(r1040);
            var actors = s.Host.SeqPlay!.Actors!;
            foreach (var A in actors.List.Where(a => a.A["cls"].Str() == "hu_spearman"))
                t.True(A.E != null && A.E.Rec?.Name == A.A["bind"]!["name"].Str(), $"{name}: sc_1040's {A.A["name"].Str()} is the spearman {A.A["bind"]!["name"].Str()} ({(A.E == null ? "drawn for the scene only" : A.E.Rec?.Name + " of " + A.E.Owner?.Id)})");
            s.Host.SequenceFinished(r1040);
            foreach (var r in recs)
            {
                var (x, y) = C.ToMap(r!.Entity!.Pos.X, r.Entity.Pos.Z);
                t.True(x > 395 && x < 412 && y > 535 && y < 552, $"{name}: {r.Name} (owner {r.Entity.Owner?.Id}) ends with Stina below the rocks ({x:0},{y:0})");
            }
            var (cx, cy) = C.ToMap(cole.Pos.X, cole.Pos.Z);
            t.True(cy < 520, $"{name}: Cole stays above the rocks ({cx:0},{cy:0})");
            // sc_1050: its four spearmen (owner 0, any name) are the player's - every seat's
            var A1050 = new SeqActors(C, C.Data.Sequences.First(kv => kv.Key.EndsWith("sc_1050.seq")).Value!["play"]!.AsObject());
            var sp = A1050.List.Where(a => a.A["cls"].Str() == "hu_spearman").ToList();
            t.True(sp.Count == 4 && sp.All(a => a.E != null) && sp.Select(a => a.E!.Rec!.Name).OrderBy(n => n).SequenceEqual(names),
                $"{name}: sc_1050's spearmen are the four freed ones: {string.Join(" ", sp.Select(a => a.E?.Rec?.Name ?? "-"))}");
        }
    }

    /// <summary>
    /// Every mission's cutscenes in a co-op game (base, seat 1 and two AI commanders; two thirds of seat 1's units of
    /// the map dealt to the other seats): an actor the scene binds to "the player" (owner 0) is never drawn for the
    /// scene only while a unit of the map given to a seat fits it (its class or the seat tribe's equivalent, its name)
    /// and no other actor took that unit (the binding takes the map's objects - those with a GUID -, not units made
    /// later: a seat's start army stays out of the story's scenes as the player's own recruits do) - the same rule the
    /// triggers use for "the player" (Mission1RockfallSplit for the case reported; without it mission 4's sc_2040 lost
    /// an archer and a spearman of the other seats too).
    /// </summary>
    public static void CoopActorsBindEverySeat(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no base configuration");
        var bad = new List<string>(); int seats = 0, n = 0;
        foreach (var e in MissionEntry.LoadIndex(c!.Config.MissionsDir))
        {
            if (WorldBuilder.FindMap(c.Config, e.Map) == null || PwRemake.Sim.Coop.CoopDefinition.Load("base", e.Id) is not { } def || !def.CampaignMode) continue;
            MissionSession s;
            try { s = CoopTests.Session(t, CoopTests.CoopStart(t, "base", e.Id, new[] { ("human", ""), ("ai", ""), ("ai", "") })); }
            catch (SkipException) { continue; }
            n++;
            var C = s.Campaign; var R = C.Coop!;
            // the map's units of seat 1 dealt round the seats (as the hero split and the gifts do)
            var others = R.Seats.Where(x => x.Seat > 1).Select(x => x.Player).ToList();
            int k = 0;
            foreach (var u in s.World.Units.Where(u => u.Alive && u.Owner == C.Human && u.Rec is { Guid: not null } && !u.IsWorker).ToList())
                if (k++ % 3 != 0) s.World.SetOwner(u, others[k % others.Count]);
            foreach (var (path, sq) in C.Data.Sequences)
            {
                if (sq?["play"] is not JsonObject pj) continue;
                var A = new SeqActors(C, pj);
                var taken = A.List.Where(x => x.E != null).Select(x => x.E!).ToHashSet();
                foreach (var a in A.List.Where(x => x.Temp && x.A["bind"]?["owner"].Int(-1) == 0))
                {
                    var cls = (a.A["bind"]?["cls"].Str() is { Length: > 0 } bc ? bc : a.A["cls"].Str()).ToLowerInvariant();
                    var nm = a.A["bind"]?["name"].Str() ?? "";
                    var fit = s.World.Units.FirstOrDefault(u => u.Alive && !taken.Contains(u) && u.Owner != null && u.Owner != C.Human && R.IsSeatSlot(u.Owner.Id) &&
                        u.Rec is { Guid: not null } && C.Objects.ClassIs(u.Rec, cls) && (nm.Length == 0 || string.Equals(u.Rec.Name, nm, StringComparison.OrdinalIgnoreCase)));
                    if (fit != null) bad.Add($"{e.Id} {TrigUtil.FilePart(path)}: {a.A["name"].Str()} ({cls}) drawn for the scene only, seat {R.SeatOf(fit.Owner)?.Seat}'s {fit.Rec!.Name} fits");
                    else if (s.World.Units.Any(u => u.Alive && u.Owner != C.Human && u.Owner != null && R.IsSeatSlot(u.Owner.Id) && u.Rec is { Guid: not null } && C.Objects.ClassIs(u.Rec, cls))) seats++;
                }
            }
        }
        foreach (var b in bad.Take(40)) t.Log("  " + b);
        t.Log($"{n} co-op missions; {seats} actors of the player with a seat's unit of their class (all taken by other actors)");
        if (n == 0) t.Skip("no missions");
        t.True(bad.Count == 0, $"{bad.Count} actors of the player not bound to a seat's unit that fits");
    }

    // Kacper: "During this cutscene characters didn't turn to the person they were talking to" (mission 10, sc_3080:
    // Stina and the warden). A recorded path's turn keys were read at the time of its position keys: Stina's turn to the
    // warden (23.6 s, one position key, turn keys at 0 and 0.6 s) stayed at its first key - facing away from him. Turn keys
    // go on their own times, the shorter way round, at an even pace; a walk turns into new directions instead of snapping.
    public static void TurnKeysOnTheirOwnTimes(TestContext t)
    {
        static bool Near(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI)) < 1e-6;
        var rots = new List<double[]> { new[] { 0.0, -3.1895 }, new[] { 0.6, 0.1965 } };
        t.True(Near(SeqActors.RotAt(rots, -1), -3.1895) && Near(SeqActors.RotAt(rots, 0.6), 0.1965) && Near(SeqActors.RotAt(rots, 5), 0.1965), "before the first key the first, from the last the last");
        // -3.1895 -> 0.1965 is 3.386 rad one way, 2.897 the other: half way is -3.1895 - 1.4486
        t.True(Near(SeqActors.RotAt(rots, 0.3), -3.1895 - (2 * Math.PI - 3.386) / 2), $"the shorter way round: {SeqActors.RotAt(rots, 0.3)}");
        // a walk east then north: on a leg its direction, at the waypoint half way between, turning in from where it faced
        var keys = new List<double[]> { new[] { 0.0, 0, 0, 0 }, new[] { 2.0, 10, 0, 0 }, new[] { 4.0, 10, -10, 0 } };
        double e = Geo.HeadingTo(1, 0), n = Geo.HeadingTo(0, -1);
        t.True(Near(SeqActors.WalkHeading(keys, 1, 0, true)!.Value, e) && Near(SeqActors.WalkHeading(keys, 3, 0, true)!.Value, n), "a leg's direction");
        t.True(Near(SeqActors.WalkHeading(keys, 2, 0, true)!.Value, e + Math.IEEERemainder(n - e, 2 * Math.PI) / 2), "half turned at the waypoint");
        t.True(Near(SeqActors.WalkHeading(keys, 0, 1.0, true)!.Value, 1.0) && Near(SeqActors.WalkHeading(keys, 0, 1.0, false)!.Value, e), "turning in from the heading before (not after a cut)");
        // sc_3080 at the warden's line: Stina and the warden face each other
        var s = CampaignTests.Session(t, "base", 10);
        var A = new SeqActors(s.Campaign, Play(s, "sc_3080.seq"));
        var st = A.PoseAt(Named(A, "Stina_s0_0"), 40); var wd = A.PoseAt(Named(A, "darwin_s0_2"), 40);
        double toW = Geo.HeadingTo(wd.X - st.X, wd.Z - st.Z), toS = Geo.HeadingTo(st.X - wd.X, st.Z - wd.Z);
        double offS = Math.Abs(Math.IEEERemainder(st.H - toW, 2 * Math.PI)), offW = Math.Abs(Math.IEEERemainder(wd.H - toS, 2 * Math.PI));
        t.True(offS < 0.5 && offW < 0.5, $"Stina faces the warden ({offS:0.00} rad off) and he her ({offW:0.00} rad off)");
    }

    // Kacper: "something is wrong with the gate for the holy city" (mission 13, sc_4070): a brown slab in the gate - the
    // shadow hull of hcl13_gate / hc_gate (mesh 18: render flags 19 + construction states, no pick flag, texture
    // test/gates.tga) was drawn, as a wall archive's parts carrying the shadow flag are drawn (its pieces' construction
    // parts carry it with the pick flag).
    public static void HolyCityGateShadowHull(TestContext t)
    {
        t.Equal("shadow", PwRemake.View.Parts.PartBits.HelperPart(65536031u, "Wall") ?? "drawn", "hcl13_gate mesh 18 is not drawn");
        uint wallPart = (1u << 18) | (1u << 19) | (1u << 22) | (1u << 23) | (1u << 24) | (1u << 26) | 0x1f;
        t.True(PwRemake.View.Parts.PartBits.HelperPart(wallPart, "Wall") == null, "a palisade piece's construction part (pick + shadow flags) is drawn");
    }
}
