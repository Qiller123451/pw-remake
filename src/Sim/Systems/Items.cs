// Items (artefacts): treasure chests lying on the map that a character, animal or vehicle picks up and carries; while
// carried they give their owner's tribe the bonuses of their tech tree filter.
//
// Reference: misc/Item.usl (CItem, CItemEffectInvoker, CRideCup), misc/ItemSpawn.usl (CItemSpawn, CItemMgr
// GenerateItems), task/PickUp.usl, FightingObj.usl PickUp / AddItem / RemoveItem / Delete, character.usl (an
// inventory of one item), GameInputController.usl (the "trade" cursor over an item, the right click), Info.usl (the
// item slot of the info window), Player.usl AddScalps (the scalps modifier).
//
// An item (W.Items) is an entity of kind "item" (the JS: a Prop with .item = {cls, carrier}) shown by a Prop (its
// model); def = the tech tree's Items/Objects/<class> {effect 0 on use, 1 while carried, 2 while not carried; GlobalTT
// (a filter of the carrier's player), LocalTT (of the carrier), EffectName (HealAll, ShowWorld, RepairNoCost,
// IgnoreSlope_<type>, MoreScalps), bonus}. Every item of the game is "while carried" (effect 1). A unit carries at most
// one (Unit.Item).
// The map's item spawns (ItemSpawn objects: spawn_items, generate_always, random_item; the level's Root/Items/Pool
// and MaxItems) put the items on the map when the level starts.
//
// Ported from remake/src/game/systems/items.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>An item on the map or carried (kind "item"); Prop: the model lying on the ground (hidden while carried).</summary>
public sealed partial class Item : Entity
{
    /// <summary>the item class (lower case)</summary>
    public string ItemCls;
    public Unit? Carrier;
    public Prop? Prop;

    public Item(World world, string cls, double x, double z, Prop? prop) : base(world, "item", null, x, z)
    {
        ItemCls = cls; Name = cls; Cls = "item"; Radius = 1; Prop = prop;
        if (prop != null) Pos = prop.Pos;
    }
}

/// <summary>the tech tree's description of an item class (items.js itemDef)</summary>
public sealed class ItemDef
{
    public double Effect = 1, Bonus, Range;
    public string GlobalTT = "", LocalTT = "", EffectName = "";
}

/// <summary>an item spawn of a map (CItemSpawn): its classes, generate_always, random_item</summary>
public sealed class ItemSpawn
{
    public double X, Z, Rot;
    public List<string> Items = new();
    public bool Always, Random;
}

public sealed partial class World
{
    const double ITEM_REACH = 4;                                    // PickUp.usl: arrived within 4 m
    static readonly Regex CARRIERS = new("^(CHTR|ANML|VHCL)$", RegexOptions.Compiled);   // FightingObj.PickUp
    /// <summary>items.js RepairNoCost: the player whose repairs cost nothing (character.usl ms_iRepairNoCost), null = none</summary>
    public Player? RepairNoCost;

    /// <summary>+v of the JS (Number()): NaN when it is no number</summary>
    static double JsNumber(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : s.Trim() == "" ? 0 : double.NaN;

    public ItemDef ItemDefOf(string cls)
    {
        var n = Data.ItemNode(cls);
        string V(string k, string d) => n != null && n.TryGetValue(k, out var x) && x != null && x != "" ? x : d;
        return new ItemDef { Effect = JsNumber(V("effect", "1")), GlobalTT = V("GlobalTT", ""), LocalTT = V("LocalTT", ""), EffectName = V("EffectName", ""), Bonus = JsNumber(V("bonus", "0")), Range = JsNumber(V("range", "0")) };
    }

    /// <summary>an item lying at (x, z); prop = an existing Prop (a campaign's placed item) or null; null if there is no model</summary>
    public Item? PlaceItem(string cls, double x, double z, double rot = 0, Prop? prop = null)
    {
        cls = cls.ToLowerInvariant();
        if (prop == null)
        {
            var model = ItemModel(cls);
            if (model == null) return null;
            prop = AddProp(new PropSpec { Model = model, X = x, Z = z, Rot = rot, Block = false });
        }
        var it = new Item(this, cls, x, z, prop);
        Items.Add(it);
        return it;
    }
    public string? ItemModel(string cls)
    {
        foreach (var m in new[] { Data.ClassGfx(cls), "item_treasure_chest" }) if (m != null && Models.Has(m)) return m;
        return null;
    }
    /// <summary>the item a prop shows (the JS: the prop itself is the item, prop.item); null: none</summary>
    public Item? ItemOfProp(Prop p) { foreach (var it in Items) if (it.Prop == p) return it; return null; }
    public bool CanCarry(Unit? u) => u != null && u.Alive && CARRIERS.IsMatch(u.Cls) && !u.Inside;
    /// <summary>is there an item to pick up at t (o.target.item)</summary>
    public bool HasItem(Entity t) => t is Item;
    /// <summary>an item lying where a unit can pick it up: not carried and shown - a map's hidden item (missions 6 and
    /// 12: the admiral's hat, the ankh until ObjAppear shows them) is not there yet for anyone</summary>
    public bool ItemLying(Item it) => it.Alive && it.Carrier == null && (it.Prop == null || it.Prop.Visible);

    /// <summary>CItemMgr.GenerateItems: the spawns that always make an item first (in the map's order), then the others in a
    /// random order, until the spawns made MaxItems items; no class twice among them (items placed on the map do not
    /// count). pool = the level's item classes (a random spawn without a list takes from it)</summary>
    public void GenerateItems(List<ItemSpawn> spawns, List<string> pool, int max)
    {
        var used = new List<string>();
        void Make(ItemSpawn s)
        {
            var list = (s.Items.Count > 0 ? s.Items : s.Random ? pool : new List<string>()).Select(c => c.ToLowerInvariant()).Where(c => !used.Contains(c)).ToList();
            if (list.Count == 0) return;
            var cls = s.Random ? list[(int)Math.Floor(Rng.Next() * list.Count)] : list[0];
            if (PlaceItem(cls, s.X, s.Z, s.Rot) != null) used.Add(cls);
        }
        var rest = new List<ItemSpawn>();
        foreach (var s in spawns) { if (used.Count >= max) return; if (s.Always) Make(s); else rest.Add(s); }
        while (rest.Count > 0 && used.Count < max) { int i = (int)Math.Floor(Rng.Next() * rest.Count); var s = rest[i]; rest.RemoveAt(i); Make(s); }
    }

    // ---------------------------------------------------------------- picking up (task "pickup", PickUp.usl)
    partial void PickupUpdate(Unit u, double dt)
    {
        var t = u.Task; var it = t.Target as Item;
        if (it == null || !ItemLying(it)) { ToIdle(u); return; }
        if (u.Item != null) { Emit("msg", new Args { ["player"] = u.Owner, ["key"] = "_NT_PickUpFailInvFull", ["text"] = "Inventory full" }); ToIdle(u); return; }
        double d = Hypot(it.Pos.X - u.Pos.X, it.Pos.Z - u.Pos.Z);
        if (t.Phase == "take")
        {
            if ((t.Wait -= dt) > 0) return;
            TakeItem(u, it);
            ToIdle(u);
            u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z;
            return;
        }
        if (d > ITEM_REACH)
        {
            if (!t.Walking || (u.Path.Count == 0 && ++t.Tries < 4)) { u.SetPath(it.Pos.X, it.Pos.Z); t.Walking = true; }
            if (u.Path.Count == 0) { ToIdle(u); return; }
            u.MoveAnim(u.Steer(dt, u.Speed, 0.5), false);
            return;
        }
        // arrived: a character bends down ("pick_up"), then the item is its
        u.Path = new NavPath(); u.Vel = V3.Zero;
        t.Phase = "take";
        t.Wait = 0;
        if (u.Cls == "CHTR" && u.Anim!.Has("pick_up")) { u.Anim.Play("pick_up", new AnimOpts { Restart = true, Loop = false }); u.BusyAnim = true; t.Wait = Or(u.Anim.Duration("pick_up"), 1); }
    }
    /// <summary>FightingObj.AddItem / CItem.OnTaken: the item goes into the unit's inventory and works</summary>
    public bool TakeItem(Unit u, Item it)
    {
        if (!u.Alive || u.Item != null || it.Carrier != null) return false;
        u.Item = it;
        it.Carrier = u;
        it.Prop?.SetVisible(false);
        u.BusyAnim = false;
        ItemEffects(it, u, true);
        Emit("itemTaken", new Args { ["unit"] = u, ["item"] = it });
        Emit("msg", new Args { ["player"] = u.Owner, ["key"] = "_NT_Artefact_Gain", ["text"] = "Artefact found", ["item"] = it.ItemCls });
        return true;
    }
    /// <summary>FightingObj.RemoveItem / CItem.OnDropped: the item falls where the unit stands - into deep water it is lost</summary>
    public Item? DropItem(Unit u)
    {
        var it = u.Item;
        if (it == null) return null;
        u.Item = null;
        it.Carrier = null;
        ItemEffects(it, u, false);
        Emit("itemDropped", new Args { ["unit"] = u, ["item"] = it });
        if (u.Owner != null) Emit("msg", new Args { ["player"] = u.Owner, ["key"] = "_NT_Artefact_Lost", ["text"] = "Artefact lost", ["item"] = it.ItemCls });
        double x = u.Pos.X, z = u.Pos.Z, h = Height(x, z);
        if (WaterLevel != null && h < WaterLevel.Value - 0.5) { it.Alive = false; it.Prop?.Remove(); Items.Remove(it); return null; }
        it.Pos = new V3(x, h, z);
        if (it.Prop != null) { it.Prop.Pos = it.Pos; it.Prop.SetVisible(true); }
        return it;
    }
    /// <summary>the item's effect on (taken) or off (dropped): its filters and its effect (CItem.OnTaken / OnDropped,
    /// CItemEffectInvoker). Items that work while not carried or on use (none in the game) do nothing here.</summary>
    void ItemEffects(Item it, Unit u, bool on)
    {
        var def = ItemDefOf(it.ItemCls); var p = u.Owner;
        if (def.Effect != 1) return;
        static string F(string path) => Regex.Replace(path ?? "", "^/?Filters/", "");
        if (def.LocalTT != "") LocalFilter(u, F(def.LocalTT), on);
        if (def.GlobalTT != "" && p?.Tt != null) { if (on) p.Tt.Enable(F(def.GlobalTT)); else p.Tt.Disable(F(def.GlobalTT)); }
        if (def.EffectName != "") ItemEffect(def.EffectName, def, u, on);
        if (it.ItemCls == "item_ride_cup") RideCup(u, on);
    }
    void ItemEffect(string name, ItemDef def, Unit u, bool on)
    {
        var p = u.Owner;
        int k = name.LastIndexOf('_');
        string bas = k >= 0 ? name.Substring(0, k) : name, type = k >= 0 ? name.Substring(k + 1) : "";
        switch (bas)
        {
            // the carrier's player's characters all at full hit points
            case "HealAll": if (on && p != null) foreach (var c in Units.ToList()) if (c.Alive && c.Owner == p && c.Cls == "CHTR") SetHp(c, c.MaxHp); break;
            // the whole map is seen for a moment (a ShowFOW_Obj of the map's size, gone after 0.1 s): explored
            case "ShowWorld": if (on && p != null) RevealArea(p, 0, 0, Map.Size, 0.1); break;
            // repairs cost the carrier's player nothing (character.usl ms_iRepairNoCost)
            case "RepairNoCost": RepairNoCost = on && p != null ? p : null; break;
            // units of the type walk any slope (SetIgnoreSlope): no unit of the game is stopped by a slope in the remake
            case "IgnoreSlope": if (p != null) foreach (var c in Units) if (c.Alive && c.Owner == p && c.Cls == type) c.IgnoreSlope = on; break;
            // the skulls the player gets are multiplied (Player.AddScalps)
            case "MoreScalps": if (p != null) p.ScalpMod = on ? def.Bonus : 1; break;
            default: break;                   // (MIRAGE's item_spores names "BoomingEconomy": no such effect in the scripts)
        }
    }
    /// <summary>CRideCup: while its carrier rides a transport (an animal, a vehicle) the transport has half its hit points more</summary>
    public void RideCup(Unit u, bool on)
    {
        if (u.InsideOf is not Unit t) return;
        TransportBonus(t, on && u.Item != null && u.Item.ItemCls == "item_ride_cup");
    }
    public void TransportBonus(Unit t, bool on)
    {
        if (t.RideCup == on) return;
        t.RideCup = on;
        double f = t.Hp / t.MaxHp;
        t.MaxHp = t.Stats!.Hp * (on ? 1.5 : 1) * (t.Grow ?? 1);
        t.Hp = Math.Min(t.MaxHp, f * t.MaxHp);
    }
    /// <summary>the scalps a player gets (Player.AddScalps: rounded, times the scalps modifier)</summary>
    public void AddScalps(Player p, double n)
    {
        double v = ScalpReward(p, n);
        p.Res["skulls"] = p["skulls"] + v;
        p.Gained["skulls"] = (p.Gained.TryGetValue("skulls", out var g) ? g : 0) + v;
    }
    /// <summary>Player.AddScalps' value: n rounded, times the player's scalps modifier, rounded</summary>
    public static double ScalpReward(Player p, double n)
    {
        double sm = p.ScalpMod != 0 && !double.IsNaN(p.ScalpMod) ? p.ScalpMod : 1;
        return Math.Floor(TT_JsRound(n) * sm + 0.5);
    }
    static double TT_JsRound(double x) => Rules.TT.JsRound(x);
}
