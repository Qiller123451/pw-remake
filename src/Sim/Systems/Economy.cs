// Economy: gathering wood, stone and food, fields and other "unlimited" buildings, delivery, storage,
// buying resources with skulls.
//
// Reference: docs/spec/economy.md (Harvest.usl, Mine.usl, GetFood.usl, GetCorn.usl, GetUnlimited.usl,
// DeliverResources.usl, Building.usl CWarehouse).
//
// Time for one load (the core formula of all gather tasks):
//   loops = trunc(K * TF * space / capacity)      space = free carrying capacity (a full load when empty)
//   K  = 5 wood, 5 stone, 3 bushes and carcasses, 5 fields and slaughterhouses
//   TF = worker time factor ("timefactor", 0 -> 2.0): wood uses only the worker's, stone/food x NODE_TF,
//        fields x the building's time factor
// Each loop is one play of the work animation, so the real duration comes from the original animation lengths.
//
// Ported from remake/src/game/systems/economy.js (pw-exporter 98c7703). The props and foliage sprites of a felled /
// used-up tree are the view's: it follows the events "treefall" / "timbergone" and the node's Alive.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

// ---------------------------------------------------------------------------------------------- state of the economy
public sealed partial class UnitTask
{
    /// <summary>gather: seconds worked on this load, loops of the work animation it takes / done, free carrying space</summary>
    public double Work, Space;
    public int Loops, LoopsDone;
    /// <summary>gather: the work animation started for this load / this chunk of a field (AnimCtl.PlayLoops) and its
    /// length; Full: the field's chunk filled the load (the pick-up follows the chunk's animation)</summary>
    public string? ActClip;
    public double ActDur;
    public bool Full;
    /// <summary>gather at a building: "field" | "unlimited" | "power"</summary>
    public string? FarmKind;
    /// <summary>gather: the pick-up / put-down wait, waiting without a storehouse, waiting at a full storehouse</summary>
    public double WaitT, NoDropT, FullT;
    public bool FullMsg;
    /// <summary>gather: the animal class hunted (the hunting chain, GetFood.usl:478)</summary>
    public string? Hunt;
    /// <summary>trade: the market the cart comes from and the one it goes to</summary>
    public Building? From, To;
}

public sealed partial class Unit
{
    /// <summary>a trade cart's home market (TransportObj.GetHome)</summary>
    public Building? TradeHome;
}

public sealed partial class Building
{
    /// <summary>fields: the grow animation's step (0..100; GetCorn.usl, CCornfield.SetGrowStep wraps after 100)</summary>
    public int GrowStep;
}

public sealed partial class ResNode
{
    /// <summary>a standing tree being felled: seconds of chopping (6 fell it)</summary>
    public double Chop;
    /// <summary>a felled tree's fall clip (chop_down_01..08) and its log's turn (a brachiosaurus' stomp: map rotation 0)</summary>
    public string? FallClip;
    public double? LogRot;
    /// <summary>a carcass: the animal class (the hunting chain) and the dead animal it is</summary>
    public string? Hunt;
    public Unit? CorpseOf;
}

/// <summary>Debug options of a player that act in the systems (game/cheats.js applyCheats: player.cheat).</summary>
public sealed class PlayerCheat
{
    /// <summary>storage limit per resource (null: the buildings')</summary>
    public double? Storage;
    public double? PopMax;
    public bool NoHousing;
    /// <summary>gathering ×, construction / training / research speed ×</summary>
    public double Gather = 1, Speed = 1;
    public double[]? Pyramid;
    public double? PopFloor;
}

public sealed partial class Player
{
    /// <summary>the debug options of this player (game/cheats.js: the human's), null: none</summary>
    public PlayerCheat? Cheat;
}

public sealed partial class World
{
    static readonly Logger LogEco = Log.For("Systems.Economy");

    /// <summary>TimeFactor/STON|FOOD|FRUI as read by the scripts (falls back to 2.0 - see spec 3.2)</summary>
    public const double NODE_TF = 2.0;
    static class K { public const double Wood = 5, Stone = 5, Food = 3, Field = 5, Unlimited = 5; }
    static readonly Dictionary<string, int> SLOTS = new() { ["CGrowingField"] = 2, ["CCornfield"] = 2, ["CUnlimitedBuilding"] = 2, ["CAjeUnlimitedBuilding"] = 4 };
    // what a worker carries (SetThing, Harvest.usl:90): stone and food in the tribe's container
    static readonly Dictionary<string, string[]> CONTAINER = new()
    {
        ["Hu"] = new[] { "Back", "hu_pannier" }, ["Aje"] = new[] { "HndR", "aje_clay_jug" },
        ["Ninigi"] = new[] { "HndR", "ninigi_basket" }, ["SEAS"] = new[] { "Back", "seas_backpack" },
    };
    /// <summary>skulls -> resources at the market: 100 skulls buy 100 food / 67 wood / 50 stone (NewPointBuyCosts.txt)</summary>
    public static readonly Dictionary<string, double> BUY_RATE = new() { ["food"] = 1.0, ["wood"] = 1.5, ["stone"] = 2.0 };
    static readonly Regex TradeScript = new("CWarehouse|CMarketplace", RegexOptions.Compiled);

    /// <summary>the minimap's forest changed (a tree fell)</summary>
    public int ForestVersion;

    static double SelfTF(Entity? e) => e?.Stats != null && e.Stats.Tf > 0 ? e.Stats.Tf : 2.0;
    static string TribeOf(Unit u) => u.Owner != null ? u.Owner.Tribe : u.Def!.Tribe;
    static string Tool(Unit u, string kind) => $"{TribeOf(u).ToLowerInvariant()}_{kind}";

    public double CarryCap(Unit u, string res)
    {
        // harvester vehicles and the lumber mammoth: a whole log per trip, 150 stone (20 for the SEAS walker) - HarvesterTask.usl
        if (u.Cls != "CHTR") return res == "wood" ? 1e6 : u.Name == "seas_mechanical_walker" ? 20 : 150;
        var c = u.Stats!.Carry;
        double v = c[res];
        return v != 0 ? v : c.Food != 0 ? c.Food : 20;
    }
    /// <summary>slots of a field / slaughterhouse</summary>
    public int FarmSlots(Building b) => b.Def!.Script != null && SLOTS.TryGetValue(b.Def.Script, out var s) ? s : b.Def.MaxWorkers > 0 ? (int)b.Def.MaxWorkers : 2;
    // (MIRAGE's punching bag has a field's "unlimited stone" in the tree but is a plain CBuilding: nobody works there -
    // GameInputController.IsUsableBuilding does not name it, character.usl sends no worker to it)
    public bool IsFarm(Entity? b) => b is Building bb && bb.Def!.Unlimited.Count > 0 && !Data.IsA(bb.Name, "CHuPunchingBag");
    /// <summary>what the worker's carrying looks like: [link, container, walk set]</summary>
    public string[] CarryLook(Unit u, string res, bool fromField)
    {
        if (fromField) return new[] { "HndR", "hu_corn", "shoulder" };
        if (res == "wood") return new[] { "HndR", ProductWood ?? "product_wood_jun", "shoulder" };
        var c = CONTAINER.TryGetValue(TribeOf(u), out var cc) ? cc : CONTAINER["SEAS"];
        return new[] { c[0], c[1], "carry" };
    }

    // ------------------------------------------------------------------ orders
    partial void StartGather(Unit u, Entity? target) => StartGatherAt(u, target);

    /// <summary>a worker goes to gather at a node, a field or a slaughterhouse (economy.js startGather)</summary>
    public void StartGatherAt(Unit u, Entity? node)
    {
        if (!u.IsWorker || node == null || !node.Alive) return;
        // fish shoals: fishing boats only; fishing boats: fish shoals only (naval.md §4)
        if ((node is ResNode wn && wn.Water) || u.Naval) { if (node is ResNode fn && fn.Water) StartFishing(u, fn); return; }
        // vehicles and animals only harvest wood and stone (GetFood ends at once for them)
        if (u.Cls != "CHTR" && (node.Kind == "building" || (node is ResNode rf && rf.Res == "food"))) return;
        if (node is Building farm)
        {
            if (!IsFarm(farm) || !farm.Built || farm.Owner != u.Owner) return;
            if (farm.Workers.Count >= FarmSlots(farm) && !farm.Workers.Has(u))
            {
                // field full: try another ready field of the same kind within 50 m (GetCorn.usl)
                var other = Buildings.FirstOrDefault(b => b != farm && b.Alive && b.Built && b.Owner == u.Owner && b.Name == farm.Name && b.Workers.Count < FarmSlots(b) && b.DistTo(farm) < 50);
                if (other == null) return;
                farm = other;
            }
            farm.Workers.Add(u);
            u.Task = new UnitTask("gather") { Farm = farm, Res = farm.Def!.Unlimited[0], Phase = "go", Work = 0, User = u.Task.User };
            var (fx, fz) = ApproachPoint(u, farm);
            u.SetPath(fx, fz);
            return;
        }
        if (node is not ResNode n) return;
        n.Workers.Add(u);
        u.Task = new UnitTask("gather") { Node = n, Res = n.Res, Phase = "go", Work = 0, User = u.Task.User, Hunt = n.Hunt ?? u.Task.Hunt };
        u.SetPath(n.Pos.X, n.Pos.Z);
    }

    // ------------------------------------------------------------------ the gather task
    partial void GatherUpdate(Unit u, double dt)
    {
        var t = u.Task;
        switch (t.Phase)
        {
            case "go": GatherGo(u, t, dt); return;
            case "chop": GatherChop(u, t, dt); return;
            case "work": GatherWork(u, t, dt); return;
            case "pickup": case "putdown": GatherWait(u, t, dt); return;
            case "deliver": GatherDeliver(u, t, dt); return;
        }
    }

    void GatherGo(Unit u, UnitTask t, double dt)
    {
        Entity? target = (Entity?)t.Node ?? t.Farm;
        if (target == null || !target.Alive || (t.Node != null && t.Node.Amount <= 0) || (t.Farm != null && !t.Farm.Built)) { FindNextNode(u); return; }
        bool near = t.Node != null ? u.DistTo(target) <= t.Node.Radius + u.Radius + 0.8 : u.EdgeDist(target) <= 1.5 || t.Farm!.Contains(u.Pos.X, u.Pos.Z, 0.5);
        if (near || u.Path.Count == 0) { BeginWork(u, t); return; }
        double sp = u.Steer(dt, u.Speed);
        u.MoveAnim(sp, false);
    }

    // arrived: work out how many loops this load takes
    void BeginWork(Unit u, UnitTask t)
    {
        u.Path = new NavPath(); u.Vel = V3.Zero;
        t.Work = 0; t.LoopsDone = 0; t.ActClip = null; t.ActDur = 0; t.Full = false;
        var node = t.Node; var farm = t.Farm;
        var res = t.Res!;
        double cap = CarryCap(u, res);
        double have = u.Carry != null && u.Carry.Res == res ? u.Carry.Amount : 0;
        if (u.Carry != null && u.Carry.Res != res) u.SetCarry(null, 0, null);
        double space = Math.Max(0, cap - have);
        t.Space = space;
        if (u.Cls != "CHTR")
        {
            // vehicles: one cut fells a tree, one "take" loads the log; one mining animation per load of stone
            if (node != null && node.Type == "tree") FellTree(node);
            t.Loops = 1; t.Phase = "work"; t.Space = res == "wood" ? node!.Amount : space;
            return;
        }
        if (node != null && node.Type == "tree") { t.Phase = "chop"; return; }
        double k, tf;
        if (farm != null && res == "power")
        {
            // the sequel's power plants: the workers keep the plant running, the power goes into the grid (no carrying)
            if (u.Carry != null) u.SetCarry(null, 0, null);
            t.FarmKind = "power"; t.Loops = 1; t.Phase = "work";
            return;
        }
        if (farm != null)
        {
            bool unlimited = farm.Def!.Script == "CAjeUnlimitedBuilding" || farm.Def.Script == "CUnlimitedBuilding";
            k = unlimited ? K.Unlimited : K.Field; tf = SelfTF(u) * SelfTF(farm);
            t.FarmKind = unlimited ? "unlimited" : "field";
            // the slaughterhouse credits the whole load first, then plays its animation (GetUnlimited.usl bash_food)
            if (unlimited) u.SetCarry(res, cap, CarryLook(u, res, true));
        }
        else if (res == "wood") { k = K.Wood; tf = SelfTF(u); }
        else if (res == "stone") { k = K.Stone; tf = SelfTF(u) * NODE_TF; }
        else { k = K.Food; tf = SelfTF(u) * NODE_TF; }
        t.Loops = Math.Max(1, (int)Math.Truncate(k * tf * space / Math.Max(1, cap)));
        t.Phase = "work";
    }

    // felling a standing tree: 6 chops of 1 s (trees have 30 hp, a chop takes 5) - Harvest.usl:654
    void GatherChop(Unit u, UnitTask t, double dt)
    {
        var node = t.Node;
        if (node == null || !node.Alive) { FindNextNode(u); return; }
        if (node.Type != "tree") { BeginWork(u, t); return; }
        u.Face(node.Pos.X, node.Pos.Z, dt);
        u.AttachTool(Tool(u, "axe"));
        // SetSLEAnim("chop_tree", 9999, intro, outro): its start part once, the loop part a blow a second (the loop part is
        // 1 s), the end part when the tree falls (AnimActionStopLoop) - GatherWork waits for it
        u.Anim!.Play(u.Anim.Pick("chop_tree", "hacking", "chop_0"), new AnimOpts());
        node.Chop += dt;
        if (node.Chop >= 6) { FellTree(node); u.Anim.StopLoop(); BeginWork(u, t); }
    }

    void GatherWork(Unit u, UnitTask t, double dt)
    {
        var node = t.Node; var farm = t.Farm;
        if (node != null && (!node.Alive || node.Amount <= 0)) { FindNextNode(u); return; }
        if (farm != null && (!farm.Alive || !farm.Built)) { FindNextNode(u); return; }
        if (node != null) u.Face(node.Pos.X, node.Pos.Z, dt);
        var res = t.Res!;
        var A = u.Anim!;
        // vehicles (one cut / take / mining animation per load) and the sequel's power plants: the work clip loops
        if (u.Cls != "CHTR" || (farm != null && t.FarmKind == "power")) { VehicleWork(u, t, dt); return; }
        // fields: chunks of 2 .. 5 loops (GetCorn.usl harvest_corn)
        if (farm != null && t.FarmKind == "field") { FieldWork(u, t, farm, dt); return; }
        string? clip = WorkClip(u, t);
        if (t.ActClip == null)
        {
            // the end part of the chopping (the tree fell) plays out first: the original's AnimActionStopLoop is an action
            if (A.Phase == "e" && !A.Done && A.CurName != clip) return;
            // AnimAction(clip, loops, intro, outro) (Harvest.usl:846, Mine.usl:300, GetFood.usl:343, GetUnlimited.usl:184):
            // the start part, `loops` x the loop part, the end part; the load is taken when it has run
            t.ActClip = clip ?? "";
            t.ActDur = clip != null ? A.PlayLoops(clip, t.Loops) : 0;
            if (t.ActDur <= 0) t.ActDur = t.Loops * 1.2;
            t.Work = 0;
        }
        t.Work += dt;
        if (t.Work < t.ActDur) return;
        // a full load
        if (farm != null) { PickUp(u, t, res, true, t.FarmKind == "unlimited"); return; }
        double amt = t.Space;
        amt = Math.Min(amt, node!.Amount);
        if (!node.Infinite) node.Amount -= amt;          // (the remake's infinite resources are never used up)
        if (node.Type == "corpse") node.RotT = 120;      // every harvest resets the rot timer (Resource.usl)
        if (node.Amount <= 0.5) DepleteNode(node);
        double have2 = u.Carry != null && u.Carry.Res == res ? u.Carry.Amount : 0;
        u.SetCarry(res, have2 + amt, CarryLook(u, res, false));
        PickUp(u, t, res, false);
    }

    /// <summary>the worker's work clip and tool for this load (the tasks' AnimAction / SetLinkGFX)</summary>
    string? WorkClip(Unit u, UnitTask t)
    {
        var A = u.Anim!; var farm = t.Farm; var node = t.Node; var res = t.Res!;
        if (farm != null && t.FarmKind == "unlimited") { u.AttachTool(null); return A.Pick("potter_ground", "harvesting_bush"); }
        if (res == "wood") { u.AttachTool(Tool(u, "axe")); return A.Pick("hacking_dirt", "hacking", "chop_tree"); }
        if (res == "stone") { u.AttachTool(Tool(u, "pick")); return A.Pick("hacking_stone", "hacking"); }
        if (node != null && node.Type == "corpse") { u.AttachTool(null); return A.Pick("potter_ground", "harvesting_bush"); }
        u.AttachTool("hu_seed_basket", "HndL");
        return A.Pick("harvesting_bush", "potter");
    }

    // vehicles and the power plants: the work clip loops, a load / a power credit per loop of the whole clip
    void VehicleWork(Unit u, UnitTask t, double dt)
    {
        var A = u.Anim!; var farm = t.Farm; var node = t.Node; var res = t.Res!;
        string? clip;
        if (u.Cls != "CHTR") clip = A.Pick("harvest", "work_0", "attack_front", "standanim");
        else { clip = A.Pick("hacking_dirt", "hacking", "potter_ground"); u.AttachTool(null); }
        A.Play(clip);
        double dur = A.Duration(clip);
        double loopT = Math.Max(0.5, dur > 0 ? dur : 1.2);
        t.Work += dt;
        // power plants (game/sequel/power.js): each work loop is credited to the grid; the worker stays at the plant
        if (farm != null && t.FarmKind == "power")
        {
            if (t.Work >= loopT) { t.Work -= loopT; Hooks.Power?.Invoke(u, farm, loopT); }
            return;
        }
        if (t.Work < t.Loops * loopT) return;
        double amt = Math.Min(t.Space, node!.Amount);
        if (!node.Infinite) node.Amount -= amt;
        if (node.Amount <= 0.5) DepleteNode(node);
        double have = u.Carry != null && u.Carry.Res == res ? u.Carry.Amount : 0;
        u.SetCarry(res, have + amt, CarryLook(u, res, false));
        PickUp(u, t, res, false);
    }

    /// <summary>
    /// A field (GetCorn.usl harvest_corn): each chunk is 2 + Random % 4 loops (the last one cut to what the load still
    /// takes, + 1); its harvest goes into the load at its start, the field's grow clip shows the frame of its grow step
    /// (SetAnim("grow", 0, step): a still frame, one step a chunk, 0 .. 100), and the worker plays the chunk as
    /// AnimAction(sowing | scything, loops, intro, outro) - sowing with the seed basket while the step is below 25. The
    /// load full, the pick-up follows the chunk's animation.
    /// </summary>
    void FieldWork(Unit u, UnitTask t, Building farm, double dt)
    {
        var A = u.Anim!; var res = t.Res!;
        if (t.ActClip != null) { t.Work += dt; if (t.Work < t.ActDur) return; }
        bool mine = IsMineField(farm);
        if (t.Full) { u.SetCarry(res, CarryCap(u, res), CarryLook(u, res, !mine)); PickUp(u, t, res, true); return; }
        double cap = CarryCap(u, res);
        double step = cap / (K.Field * SelfTF(u) * SelfTF(farm));
        double have = u.Carry != null && u.Carry.Res == res ? u.Carry.Amount : 0;
        double space = Math.Max(0, cap - have);
        int loops = 2 + (int)Math.Floor(Rng.Next() * 4);
        if (step * loops > space) loops = (int)(space / step) + 1;
        double add = Math.Min(step * loops, space);
        if (add > 0) u.Carry = new Carry { Res = res, Amount = have + add, Look = u.Carry?.Look };
        t.Full = have + add >= cap - 1e-6;
        if (farm.Anim != null && farm.Anim.Has("grow")) farm.Anim.Still("grow", farm.GrowStep / 25.0);
        farm.GrowStep = farm.GrowStep + 1 > 100 ? 0 : farm.GrowStep + 1;
        bool sow = farm.GrowStep < 25;
        // MIRAGE's mines (hu_mine, seas_mine - GetCorn.usl): the worker's pick and "hacking_stone" (MirageClasses.cs)
        if (mine) u.AttachTool(MinePick(u), "HndR");
        else u.AttachTool(sow ? "hu_seed_basket" : "hu_sickle", sow ? "HndL" : "HndR");
        var clip = mine ? A.Pick("hacking_stone", "hacking") : A.Pick(sow ? "sowing" : "scything", "harvesting_bush", "raking", "hacking");
        t.ActClip = clip ?? "";
        t.ActDur = clip != null ? A.PlayLoops(clip, loops) : 0;
        if (t.ActDur <= 0) t.ActDur = loops * 1.2;
        t.Work = 0;
    }

    // shoulder_pick_up (wood, fields) / pick_up (stone, food), then off to the storehouse
    void PickUp(Unit u, UnitTask t, string res, bool fromField, bool unlimited = false)
    {
        u.AttachTool(null);
        if (u.Cls != "CHTR") { t.Phase = "deliver"; t.Drop = null; return; }
        var A = u.Anim!;
        // wood and fields: shoulder_pick_up; stone, food and the slaughterhouse (GetUnlimited.usl:197 AnimAction("pick_up")): pick_up
        var pu = A.Pick((res == "wood" || fromField) && !unlimited ? "shoulder_pick_up" : "pick_up", "pick_up");
        t.Phase = "pickup"; t.Drop = null; t.WaitT = pu != null ? Math.Min(1.6, A.Duration(pu)) : 0;
        if (res != "wood" && !fromField) t.WaitT = Math.Min(t.WaitT, 0.6);
        if (pu != null) { u.BusyAnim = true; A.Play(pu, new AnimOpts { Loop = false, Restart = true, Fade = 0.15, OnDone = new AnimDone("busy") }); }
    }

    void GatherWait(Unit u, UnitTask t, double dt)
    {
        t.WaitT -= dt;
        if (t.WaitT > 0) return;
        u.BusyAnim = false;
        if (t.Phase == "pickup") { t.Phase = "deliver"; return; }
        // after putting the load down: back to the same node / field, or look for the next one
        if (t.Farm != null && t.Farm.Alive) { t.Phase = "go"; var (fx, fz) = ApproachPoint(u, t.Farm); u.SetPath(fx, fz); return; }
        var back = t.Node != null && t.Node.Alive && t.Node.Amount > 0 ? t.Node : null;
        if (back != null) { t.Phase = "go"; u.SetPath(back.Pos.X, back.Pos.Z); return; }
        FindNextNode(u);
    }

    static bool IsDropoffE(Entity? e) => e is Building b ? b.IsDropoff : e is Unit u && u.IsDropoff;

    void GatherDeliver(Unit u, UnitTask t, double dt)
    {
        if (u.Carry == null) { t.Phase = "go"; return; }
        if (t.Drop == null || !t.Drop.Alive || !IsDropoffE(t.Drop))
        {
            t.Drop = NearestDropoff(u, u.Carry.Res);
            if (t.Drop == null)
            {
                u.Vel = V3.Zero; if (!u.BusyAnim) u.Anim!.Play(u.StandAnim());
                t.NoDropT += dt; if (t.NoDropT > 3) ToIdle(u);
                return;
            }
            var (x, z) = ApproachPoint(u, t.Drop);
            u.SetPath(x, z);
        }
        if (u.EdgeDist(t.Drop) < 2.2 || (u.Path.Count == 0 && u.EdgeDist(t.Drop) < 5))
        {
            var res = u.Carry.Res;
            double left = Gain(u.Owner!, res, u.Carry.Amount);
            if (left >= u.Carry.Amount - 1e-6)
            {
                // storage full: wait at the storehouse and try again every 2 s (Harvest.usl:1002)
                u.Path = new NavPath(); u.Vel = V3.Zero;
                if (!u.BusyAnim) u.Anim!.Play(u.StandAnim());
                t.FullT += dt;
                if (!t.FullMsg) { t.FullMsg = true; Emit("storagefull", new Args { ["player"] = u.Owner, ["res"] = res, ["unit"] = u }); }
                return;
            }
            t.FullT = 0; t.FullMsg = false;
            if (left > 0) { u.Carry.Amount = left; return; }
            if (t.Drop is Building db) db.OnDelivery();
            u.SetCarry(null, 0, null);
            Emit("delivered", new Args { ["unit"] = u, ["res"] = res });
            var A = u.Anim!;
            // wood and fields: shoulder_put_down; stone, food and the slaughterhouse (GetUnlimited.usl:236): put_down
            var pd = u.Cls != "CHTR" ? null : A.Pick((res == "wood" || t.Farm != null) && t.FarmKind != "unlimited" ? "shoulder_put_down" : "put_down", "belly_put_down", "put_down");
            t.Phase = "putdown"; t.WaitT = pd != null ? Math.Min(1.2, A.Duration(pd)) : 0; u.Path = new NavPath(); u.Vel = V3.Zero;
            if (res != "wood" && t.Farm == null) t.WaitT = Math.Min(t.WaitT, 0.6);
            if (pd != null) { u.BusyAnim = true; A.Play(pd, new AnimOpts { Loop = false, Restart = true, Fade = 0.15, OnDone = new AnimDone("busy") }); }
            return;
        }
        if (t.Drop.Kind == "unit" && u.Path.Count == 0) { var (x, z) = ApproachPoint(u, t.Drop); u.SetPath(x, z); }
        double sp = u.Steer(dt, u.Speed);
        u.MoveAnim(sp, false);
    }

    /// <summary>a load reaches the storehouse: a computer player's gather factor (CAiPlayer.AddResource, Player.usl:600)
    /// multiplies it; -> the part of the load that did not fit (a remainder below one unit is dropped)</summary>
    public double Gain(Player p, string res, double amount)
    {
        double g = (p.AiMods != null ? (p.AiMods.Gather != 0 ? p.AiMods.Gather : 1) : 1) * (p.Cheat != null ? p.Cheat.Gather : 1), before = p[res];
        double left = p.Deliver(res, amount * g, Time);
        if (p.Gained.ContainsKey(res)) p.Gained[res] += Math.Max(0, p[res] - before);     // (statistics: what was stored)
        return g == 1 ? left : left < 1 ? 0 : left / g;
    }

    /// <summary>search radius: wood 64 m around the last tree, stone 100 m, food 50 m (the scripts' search_for_jobs)</summary>
    public void FindNextNode(Unit u)
    {
        var t = u.Task;
        var res = t.Res;
        var from = t.Node != null ? t.Node.Pos : t.Farm != null ? t.Farm.Pos : u.Pos;
        t.Node?.Workers.Delete(u);
        if (t.Farm != null)
        {
            if (t.Farm.Alive && t.Farm.Built) { t.Phase = "go"; return; }
            t.Farm.Workers.Delete(u); t.Farm = null;
        }
        double radius = res == "stone" ? 100 : res == "wood" ? 64 : 50;
        var n = NearestResource(from.X, from.Z, res, radius, t.Node);
        // hunting chain (GetFood.usl:478): out of carcasses -> attack the next animal of the same kind within 50 m
        if (n == null && res == "food" && t.Hunt != null)
        {
            var prey = NearestAnimal(from.X, from.Z, t.Hunt, 50);
            if (prey != null && !(u.Carry != null && u.Carry.Amount > 0)) { ReleaseTask(u); u.Task = new UnitTask("attack") { Target = prey, User = true }; return; }
        }
        if (n == null && res == "food") n = NearestResource(from.X, from.Z, "food", 50, t.Node, x => x.Type != "corpse");
        if (u.Carry != null && u.Carry.Amount > 0) { t.Phase = "deliver"; t.Drop = null; t.Node = n; n?.Workers.Add(u); return; }
        if (t.Phase == "putdown" || t.Phase == "pickup") u.BusyAnim = false;
        if (n != null) { StartGatherAt(u, n); return; }
        ToIdle(u);
        u.AttachTool(null);
    }

    /// <summary>the nearest node of a resource within r with a free place (water: fish shoals only - the boats' search)</summary>
    public ResNode? NearestResource(double x, double z, string? res, double r, ResNode? exclude = null, Func<ResNode, bool>? filter = null, bool water = false)
    {
        ResNode? best = null; double bd = r;
        RHash.Query(x, z, r, n =>
        {
            if (!n.Alive || n.Res != res || n == exclude || n.Amount <= 0) return;
            if (n.Water != water) return;          // fish shoals only for boats (filter.water)
            if (n.Workers.Count >= n.MaxWorkers) return;
            if (filter != null && !filter(n)) return;
            double d = Math.Sqrt((n.Pos.X - x) * (n.Pos.X - x) + (n.Pos.Z - z) * (n.Pos.Z - z)) + n.Workers.Count * 3;
            if (d < bd) { bd = d; best = n; }
        });
        return best;
    }

    public Unit? NearestAnimal(double x, double z, string name, double r)
    {
        Unit? best = null; double bd = r;
        UHash.Query(x, z, r, a => { if (!a.Alive || !a.Wild || a.Name != name) return; double d = Math.Sqrt((a.Pos.X - x) * (a.Pos.X - x) + (a.Pos.Z - z) * (a.Pos.Z - z)); if (d < bd) { bd = d; best = a; } });
        return best;
    }

    /// <summary>storehouses that accept this resource (buildings under construction don't)</summary>
    public Entity? NearestDropoff(Unit u, string res)
    {
        Entity? best = null; double bd = 1e9;
        void Test(Entity e)
        {
            if (!e.Alive || e.Parked || e.Owner == null || u.Owner == null || !u.Owner.IsFriend(e.Owner) || !IsDropoffE(e) || !e.Def!.Delivery.Contains(res)) return;
            double d = u.DistTo(e);
            if (d < bd) { bd = d; best = e; }
        }
        foreach (var b in Buildings) Test(b);
        foreach (var x in Units) if (x.IsDropoff) Test(x);
        return best;
    }

    /// <summary>trees: standing tree -> timber log after 6 chops (vegetation.usl CTree.Fall): the stump stays, the log
    /// falls with chop_down_01..03 (CWood.OnInit: Random % 3 + 1), or with the clip a brachio's stomp chose (0..7, away
    /// from it; its logs lie at map rotation 0)</summary>
    /// <summary>a tree that lies felled from the start (map attribute fallen = 1, WorldBuilder): a log without the fall -
    /// no "treefall" event (no leaves, no stump, no shake); the view draws its log at rest (ResNode.Fallen)</summary>
    public void FellTreeQuiet(ResNode node)
    {
        if (node.Type != "tree") return;
        node.Type = "timber";
        node.FallClip ??= "chop_down_01";
        node.Chop = 0;
        if (node.CellsBlocked != null) Nav.Mark(node.CellsBlocked, -1);
        node.CellsBlocked = new List<int>();
        node.Radius = 2.2;
        node.MaxWorkers = 5;
        node.LookChanged();
    }

    public void FellTree(ResNode node, int? clip = null)
    {
        if (node.Type != "tree") return;
        node.Type = "timber";
        if (node.FallClip == null)
        {
            node.FallClip = clip != null ? "chop_down_0" + clip.Value : "chop_down_0" + (1 + unchecked((uint)(node.Id * (int)2654435761u)) % 3);
            if (clip != null) node.LogRot = Math.PI;
        }
        node.Chop = 0;
        ForestVersion++;                                  // (the minimap's forest)
        // (the tree's prop and foliage sprites go, the stump appears: the view, on "treefall")
        if (node.CellsBlocked != null) Nav.Mark(node.CellsBlocked, -1);
        node.CellsBlocked = new List<int>();
        node.Radius = 2.2;
        node.MaxWorkers = 5;                              // at most 5 workers on one log (Resource.usl:212)
        node.LookChanged();
        Emit("treefall", new Args { ["node"] = node });
    }

    partial void DepleteNode(ResNode n) => DepleteNodeNow(n);

    /// <summary>a resource node is used up / removed (economy.js depleteNode)</summary>
    public void DepleteNodeNow(ResNode node)
    {
        if (!node.Alive) return;
        node.Alive = false;
        RHash.Remove(node);
        if (node.Type == "timber") Emit("timbergone", new Args { ["node"] = node });
        // (its prop and sprites: the view, on Alive)
        if (node.CellsBlocked != null && node.CellsBlocked.Count > 0) Nav.Mark(node.CellsBlocked, -1);
        if (node.CorpseOf != null) node.CorpseOf.CorpseGone = true;
        node.LookChanged();
        foreach (var w in node.Workers.ToList()) if (w.Alive && w.Task.Node == node && w.Task.Phase != "deliver") FindNextNode(w);
    }

    partial void RecomputeCaps(Player? p) => RecomputeCapsOf(p);

    /// <summary>storage and population limits (Player.UpdateLimits): sum of finished limit buildings, at least 300 per resource</summary>
    public void RecomputeCapsOf(Player? p)
    {
        if (p == null) return;
        double mu = 0, cf = 0, cw = 0, cs = 0;
        void Add(Entity e)
        {
            var L = e.Stats!.Limits;
            mu += L.MaxUnits;
            cf += L.MaxFood; cw += L.MaxWood; cs += L.MaxStone;
        }
        foreach (var b in Buildings) if (b.Alive && b.Owner == p && b.Built) Add(b);
        foreach (var u in Units) if (u.Alive && u.Owner == p && u.Stats != null && (u.Stats.Limits.MaxUnits != 0 || u.Stats.Limits.MaxFood != 0)) Add(u);
        // (debug options, game/cheats.js: no houses needed - the limit itself; a storage limit of at least that much)
        if (p.Cheat != null && p.Cheat.NoHousing) mu = p.PopMax;
        p.MaxUnits = Math.Max(0, Math.Min(p.PopMax, mu + (p.AiMods != null ? p.AiMods.UnitLimit : 0)));
        double st = p.Cheat?.Storage ?? 0;
        if (p.CapsFixed) { if (st != 0) foreach (var r in Rules.Rules.RES) p.Caps[r] = Math.Max(p.Caps[r], st); return; }   // storage limits set by a mission (world.setCaps)
        var before = new Dictionary<string, double>(p.Caps);
        var caps = new Dictionary<string, double> { ["food"] = cf, ["wood"] = cw, ["stone"] = cs };
        foreach (var r in Rules.Rules.RES) p.Caps[r] = Math.Max(300, Math.Max(caps[r], st));
        // losing a storehouse cuts the stock down to the new limit
        foreach (var r in Rules.Rules.RES) if (p.Caps[r] < before[r] && p[r] > p.Caps[r]) p[r] = p.Caps[r];
    }

    // ------------------------------------------------------------------ trade carts (Trade.usl)
    // A cart sent to a friendly market (Dock) shuttles between it and its home market; each arrival pays the market's
    // owner win = max(1, 5 * (distance / 50) ^ 1.32) of every resource: the price divisor is the flying trader's, whose
    // prices are never set (CFlyingTraderMgr.OnInit is commented out in ServerApp.usl) - so 1 for food, wood and stone.
    public bool IsTradeBuilding(Entity? b) => b is Building bb && bb.Built && bb.Alive && TradeScript.IsMatch(bb.Def!.Script ?? "");

    /// <summary>the cart's home (TransportObj.GetHome): the market it came from, else the nearest of its owner's</summary>
    public Building? TradeHomeOf(Unit u)
    {
        if (!IsTradeBuilding(u.TradeHome) || u.TradeHome!.Owner != u.Owner)
            u.TradeHome = Buildings.Where(b => IsTradeBuilding(b) && b.Owner == u.Owner).OrderBy(b => b.DistTo(u)).FirstOrDefault();
        return u.TradeHome;
    }

    partial void StartTrade(Unit u, Entity? target) => TryStartTrade(u, target);

    public bool TryStartTrade(Unit u, Entity? target)
    {
        var home = TradeHomeOf(u);
        if (home == null || !IsTradeBuilding(target) || target!.Owner == null || !u.Owner!.IsFriend(target.Owner)) return false;
        if (target == home) { var (hx, hz) = ApproachPoint(u, home); u.Task = new UnitTask("move") { X = hx, Z = hz, User = true }; u.SetPath(hx, hz); return true; }
        u.Task = new UnitTask("trade") { From = home, To = (Building)target, User = true };
        var (x, z) = ApproachPoint(u, target); u.SetPath(x, z);
        return true;
    }

    partial void TradeUpdate(Unit u, double dt)
    {
        var t = u.Task;
        if (!IsTradeBuilding(t.To) || !IsTradeBuilding(t.From)) { ToIdle(u); return; }
        if (u.EdgeDist(t.To!) > 3 && u.Path.Count > 0) { double sp = u.Steer(dt, u.Speed); u.MoveAnim(sp, false); return; }
        if (u.EdgeDist(t.To!) > 8) { var (ax, az) = ApproachPoint(u, t.To!); u.SetPath(ax, az); if (u.Path.Count == 0) ToIdle(u); return; }
        double D = Math.Sqrt((t.To!.Pos.X - t.From!.Pos.X) * (t.To.Pos.X - t.From.Pos.X) + (t.To.Pos.Z - t.From.Pos.Z) * (t.To.Pos.Z - t.From.Pos.Z));
        double win = Math.Max(1, 5 * SMath.Pow(D / 50, 1.3219));
        var p = t.To.Owner!;
        foreach (var r in Rules.Rules.RES) p.Deliver(r, win, Time);
        Emit("traded", new Args { ["unit"] = u, ["amount"] = win, ["building"] = t.To });
        (t.From, t.To) = (t.To, t.From);
        u.TradeHome = t.From;                                  // SetHome(target): the market reached is its home now
        var (x, z) = ApproachPoint(u, t.To); u.SetPath(x, z);
    }

    /// <summary>CWarehouse.Buy: up to 100 skulls -> resources (limited by free storage, counted in skulls); null = done,
    /// else "skulls" | "storage"</summary>
    public string? BuyResource(Player p, string res)
    {
        if (p["skulls"] <= 0) return "skulls";
        if (p[res] >= p.Caps[res]) return "storage";
        double amount = Math.Min(100, Math.Min(p["skulls"], p.Caps[res] - p[res]));
        p[res] += Math.Floor(amount / BUY_RATE[res] + 0.5);       // (Math.round)
        p["skulls"] -= amount;
        return null;
    }
}
