// The tables of gamedata.json (the toolkit's pipeline/build_data.py: what the original keeps in script and settings
// files beside the tech tree): help texts, interface strings, idle animation sets, multi-part objects (composites),
// start presets, script classes and their parents, class models, resource values, the hero list, the army
// pyramid, the skull costs of level-ups, the buff table, the wall classes, the point-buy prices ... The rules read
// them as "extra" (Rules.Extra); systems read the ones they need from here.
//
// Ported from remake/src/game/data.js (pw-exporter 98c7703). data.js itself is the remake's first wrapper of an older
// gamedata format (objects / weapons tables) that main.js no longer loads; its constants (RES, SPEED_FALLBACK) live in
// Rules.cs. This file is the typed loading of today's gamedata.json, which main.js hands to `new Rules(tt, json)`.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Rules;

/// <summary>A help item (help.json / main_uk.ltf through build_data.py): what the info window and tooltips show.</summary>
public sealed class TextEntry
{
    public string Name = "";
    public string Cls = "";
    /// <summary>(null: the help item has none)</summary>
    public string? Medium = "";
    public string? Long = "";
    /// <summary>"vs" table: chtr / anml / vhcl / ship / bldg -> "+", "-", "++" ...</summary>
    public Dictionary<string, string> Vs = new(StringComparer.Ordinal);
    /// <summary>heroes: what each level brings ("1" .. "5")</summary>
    public Dictionary<string, string> Lvl = new(StringComparer.Ordinal);

    public static TextEntry From(JsonNode? n)
    {
        var t = new TextEntry
        {
            Name = n?["name"].Str() ?? "", Cls = n?["cls"].Str() ?? "",
            Medium = n?["medium"] is JsonValue m ? m.Str() : null, Long = n?["long"] is JsonValue l ? l.Str() : null,
        };
        if (n?["vs"] is JsonObject vs) foreach (var (k, v) in vs) t.Vs[k] = v.Str();
        if (n?["lvl"] is JsonObject lv) foreach (var (k, v) in lv) t.Lvl[k] = v.Str();
        return t;
    }
}

/// <summary>One entry of an idle set (IdleAnims.txt): the clip, loops min / max, weight.</summary>
public readonly record struct IdleEntry(string Anim, int MinLoops, int MaxLoops, double Weight);

/// <summary>
/// A part of a multi-part object (gamedata "composites", ModelExporter composites.json): riders, turrets, build-ups,
/// flags, wagons. {i, kind, gfx, variants, by, link, pi (parent part, -1 = the object), anim, attack_anim, cond, arg,
/// flex, weapon_class, offset}.
/// </summary>
public sealed class CompositeSpec
{
    public int I;
    public string Kind = "";
    public string Gfx = "";
    public List<string> Variants = new();
    /// <summary>"level" | "epoch" | null: which variant</summary>
    public string? By;
    public string Link = "";
    public int Pi = -1;
    public string? Anim;
    public string? AttackAnim;
    /// <summary>always | ready | construction | upgrade | invent | buildup | unless | activation | special</summary>
    public string Cond = "";
    /// <summary>arg of upgrade / invent: a filter path / an invention</summary>
    public string? ArgStr;
    /// <summary>arg of buildup / unless: part indices</summary>
    public List<int>? ArgList;
    public bool Flex;
    public string? WeaponClass;
    /// <summary>MIRAGE's linked parts with an offset (AddObjCustomized(obj, link, {x, y, z}): metres in the link's frame,
    /// GSF axes - the engine's LinkAction(parent, link, offset)); null = none</summary>
    public JsonNode? Offset;
    /// <summary>the offset in the model frame (Y up: the GSF frame turned and mirrored as the models' roots do, GSF (x, y, z)
    /// -> (-x, z, -y)); the link's own turn is left out (the links that carry offsets are not turned)</summary>
    public V3 OffsetModel;
    /// <summary>a part made finished whatever its carrier's construction level (CTower.SetTurret: SetConstructLevel(4);
    /// MIRAGE's decorations) - MirageSeasBuildings.cs PartSpecs</summary>
    public bool Whole;

    public static CompositeSpec From(JsonNode n)
    {
        var s = new CompositeSpec
        {
            I = n["i"].Int(), Kind = n["kind"].Str(), Gfx = n["gfx"].Str(), By = NullStr(n["by"]), Link = n["link"].Str(),
            Pi = n["pi"].Int(-1), Anim = NullStr(n["anim"]), AttackAnim = NullStr(n["attack_anim"]), Cond = n["cond"].Str(),
            Flex = n["flex"].Bool(), WeaponClass = NullStr(n["weapon_class"]), Offset = n["offset"]?.DeepClone(),
        };
        if (n["variants"] is JsonArray va) foreach (var v in va) s.Variants.Add(v.Str());
        if (s.Offset is JsonArray o && o.Count == 3) s.OffsetModel = new V3(-o[0].Num(), o[2].Num(), -o[1].Num());
        var arg = n["arg"];
        if (arg is JsonArray aa) { s.ArgList = new List<int>(); foreach (var v in aa) s.ArgList.Add(v.Int()); }
        else if (arg is JsonValue) s.ArgStr = arg.Str();
        return s;
    }

    static string? NullStr(JsonNode? n) => n is JsonValue ? n.Str() : null;
}

/// <summary>A tribe's start preset (gamedata "start"): units, resources, the base building.</summary>
public sealed class StartDef
{
    public List<string> Units = new();
    public Dictionary<string, double> Res = new(StringComparer.Ordinal);
    public string Base = "";
}

/// <summary>A row of the buff table (buffdecals.txt): the attribute, its decal tile, visibility, icon, tooltip.</summary>
public sealed record BuffDef(string Attrib, int Tile, int Vis, string Icon, string Tip);

public sealed class GameData
{
    static readonly Logger L = Log.For("Rules.Data");

    /// <summary>
    /// MIRAGE's pirate boss ship (Mirage.usl CPirateShip.BuildParts): its tail, sail and rowing deck are objects of their
    /// own on the hull's links psh1 / psh2 / psh3 and its cannon deck an object at psh2 - the composites table has none of
    /// them (they are no build-ups), so the hull sailed bare. Every CPirateShip class gets them as parts (kind
    /// "shippart": PirateShip.cs - their destruction level follows the ship's hit points); models from the parts' classes.
    /// </summary>
    void PirateShipParts()
    {
        string G(string cls, string d) => ClassGfx.TryGetValue(cls, out var g) && !string.IsNullOrEmpty(g) ? g.ToLowerInvariant() : d;
        foreach (var (cls, sc) in Script)
        {
            if (sc != "CPirateShip") continue;
            var list = Composites.TryGetValue(cls, out var l) ? new List<CompositeSpec>(l) : new List<CompositeSpec>();
            if (list.Any(p => p.Kind == "shippart")) continue;
            int i = list.Count == 0 ? 0 : list.Max(p => p.I) + 1;
            foreach (var (part, link, d) in new[]
            {
                ("ninigi_pirate_ship_tail", "psh1", "pirate_boss_tail"), ("ninigi_pirate_ship_sail", "psh2", "pirate_boss_sail"),
                ("ninigi_pirate_ship_row", "psh3", "pirate_boss_row"), ("ninigi_pirate_ship_cannons", "psh2", "pirate_boss_cannons"),
            })
                list.Add(new CompositeSpec { I = i++, Kind = "shippart", Gfx = G(part, d), Link = link, Cond = "always" });
            Composites[cls] = list;
        }
    }

    /// <summary>the whole file, for tables read loosely (pointBuy, finishing, particles ...)</summary>
    public readonly JsonNode Raw;
    public readonly Dictionary<string, TextEntry> Texts = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> Ui = new(StringComparer.Ordinal);
    public readonly Dictionary<string, List<IdleEntry>> Idle = new(StringComparer.Ordinal);
    public readonly Dictionary<string, List<CompositeSpec>> Composites = new(StringComparer.Ordinal);
    public readonly Dictionary<string, StartDef> Start = new(StringComparer.Ordinal);
    /// <summary>class (lower case) -> script class ("CTower", "CGrowingField" ...)</summary>
    public readonly Dictionary<string, string> Script = new(StringComparer.Ordinal);
    /// <summary>script class -> parent script class (the .usl classes)</summary>
    public readonly Dictionary<string, string> ClassParents = new(StringComparer.Ordinal);
    /// <summary>class (lower case) -> the model named in its class file</summary>
    public readonly Dictionary<string, string> ClassGfx = new(StringComparer.Ordinal);
    /// <summary>settings/Resources.txt: WOOD / STON / FRUI / FOOD -> name (lower case) -> value</summary>
    public readonly Dictionary<string, Dictionary<string, double>> Resources = new(StringComparer.Ordinal);
    public readonly List<string> Npc = new();
    public readonly double[] Pyramid = { 25, 15, 8, 3, 1 };
    public readonly double[] LevelupSkulls = { 25, 50, 100, 300 };
    public readonly Dictionary<string, double> HeroConsts = new(StringComparer.Ordinal);
    /// <summary>"" for the official game, "MIRAGE" ...</summary>
    public readonly string Mod = "";
    public readonly List<BuffDef> Buffs = new();
    public readonly List<string> WalkableWalls = new();
    /// <summary>wall class table (pipeline wall_classes): class (lower case) -> [race, class, type]</summary>
    public readonly Dictionary<string, int[]> WallClasses = new(StringComparer.Ordinal);
    /// <summary>copy -> original class (the sequel's classes)</summary>
    public readonly Dictionary<string, string> SameAs = new(StringComparer.Ordinal);
    public JsonNode? PointBuy => Raw["pointBuy"];
    public JsonNode? Finishing => Raw["finishing"];
    public JsonNode? Particles => Raw["particles"];

    public GameData(JsonNode? json)
    {
        Raw = json ?? new JsonObject();
        if (Raw["texts"] is JsonObject tx) foreach (var (k, v) in tx) Texts[k] = TextEntry.From(v);
        if (Raw["ui"] is JsonObject ui) foreach (var (k, v) in ui) Ui[k] = v.Str();
        if (Raw["idle"] is JsonObject idle)
            foreach (var (k, v) in idle)
            {
                var list = new List<IdleEntry>();
                if (v is JsonArray a) foreach (var e in a) list.Add(new IdleEntry(e?[0].Str() ?? "", e?[1].Int() ?? 1, e?[2].Int() ?? 1, e?[3].Num() ?? 1));
                Idle[k] = list;
            }
        if (Raw["composites"] is JsonObject comp)
            foreach (var (k, v) in comp)
            {
                var list = new List<CompositeSpec>();
                if (v is JsonArray a) foreach (var e in a) if (e != null) list.Add(CompositeSpec.From(e));
                Composites[k] = list;
            }
        if (Raw["start"] is JsonObject st)
            foreach (var (k, v) in st)
            {
                var s = new StartDef { Base = v?["base"].Str() ?? "" };
                if (v?["units"] is JsonArray ua) foreach (var u in ua) s.Units.Add(u.Str());
                if (v?["res"] is JsonObject ro) foreach (var (rk, rv) in ro) s.Res[rk] = rv.Num();
                Start[k] = s;
            }
        ReadMap(Raw["script"], Script);
        ReadMap(Raw["classParents"], ClassParents);
        ReadMap(Raw["classgfx"], ClassGfx);
        ReadMap(Raw["sameAs"], SameAs);
        if (Raw["resources"] is JsonObject res)
            foreach (var (k, v) in res)
            {
                var d = new Dictionary<string, double>(StringComparer.Ordinal);
                if (v is JsonObject o) foreach (var (rk, rv) in o) d[rk] = rv.Num();
                Resources[k] = d;
            }
        if (Raw["npc"] is JsonArray npc) foreach (var n in npc) Npc.Add(n.Str());
        if (Raw["pyramid"] is JsonArray py) { Pyramid = new double[py.Count]; for (int i = 0; i < py.Count; i++) Pyramid[i] = py[i].Num(); }
        if (Raw["levelup_skulls"] is JsonArray ls) { LevelupSkulls = new double[ls.Count]; for (int i = 0; i < ls.Count; i++) LevelupSkulls[i] = ls[i].Num(); }
        if (Raw["hero_consts"] is JsonObject hc) foreach (var (k, v) in hc) HeroConsts[k] = v.Num();
        Mod = Raw["mod"].Str();
        if (Raw["buffs"] is JsonArray bf)
            foreach (var b in bf)
                if (b != null) Buffs.Add(new BuffDef(b["attrib"].Str(), b["tile"].Int(), b["vis"].Int(), b["icon"].Str(), b["tip"].Str()));
        if (Raw["walkableWalls"] is JsonArray ww) foreach (var w in ww) WalkableWalls.Add(w.Str());
        if (Raw["wallClasses"] is JsonObject wc)
            foreach (var (k, v) in wc)
                if (v is JsonArray a) { var r = new int[a.Count]; for (int i = 0; i < a.Count; i++) r[i] = a[i].Int(); WallClasses[k] = r; }
        PirateShipParts();
        L.Debug(() => $"gamedata: {Texts.Count} texts, {Script.Count} script classes, {Composites.Count} composites, mod '{Mod}'");
    }

    static void ReadMap(JsonNode? n, Dictionary<string, string> into)
    {
        if (n is JsonObject o) foreach (var (k, v) in o) into[k] = v.Str();
    }

    public static GameData Load(string path) => new(Json.ParseFile(path));
}
