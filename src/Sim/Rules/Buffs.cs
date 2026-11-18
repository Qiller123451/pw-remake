// The "ranged buffs" of units and buildings: what the original's scripts mark with FightingObj.AddRangedBuff (the
// attribute BuffDecals) - the icons of the info window (Info.usl CBuffIcon: up to five, by the order of
// Scripts/Server/init/buffdecals.txt, shown to their owner and, by the buff's visibility, to everybody, enemies or
// allies) and the decals on the ground under the units (the buff's tile of Texture/Effects/UnitDecals).
//
// The remake works them out from the state of the unit (systems/effects.js: auras, bonus buckets, timed states):
//   more_damage / less_damage      CalcAttackBoni above / below the weapon's damage (FO:7880)
//   more_defense / less_defense    the temporary defence boni (AddTemporaryDefenseBoni)
//   more_range / less_range        the range bucket
//   more_bldg_damage               the building damage bucket (Babbage's aura: Hero.usl:169)
//   is_poisoned, is_held, is_camouflaged, is_invisible, is_getting_healed (2.5 s after a heal: HealMe)
//   no_animal_aggro                Darwin's aura (FO:1828), drain_life: in Livingstone's (Hero.usl:173)
//   faster_buildup                 a worker building near Tesla (BuildUpBuilding.usl:425)
//   sq_tranquilized                the sequel's dart debuff (game/sequel/rules.js adds it to the table)
//   owner_<...>                    the source of an aura: the heroes with their aura on (Hero.usl:300-320), the
//                                  buildings and units of systems/effects.js AURAS
// Data: gamedata.json "buffs" (pipeline/build_data.py buff_table): [{attrib, tile, vis, icon, tip}] in file order.
//
// Ported from remake/src/game/buffs.js (pw-exporter 98c7703). What it asks of systems/effects.js (attackBoni, tempDef,
// rangeBonus, AURAS) is World.AttackBoni / TempDef / RangeBonus / AuraOn (World.Stubs.cs until Effects is ported).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Rules;

public static class Buffs
{
    static readonly Dictionary<string, string> HERO_OWNER = new()
    {
        ["cole_s0"] = "owner_cole", ["bela_s0"] = "owner_bela", ["tesla_s0"] = "owner_faster_buildup", ["darwin_s0"] = "owner_no_animal_aggro",
        ["hermit_s0"] = "owner_healing", ["lovelace_s0"] = "owner_less_attack_speed", ["babbage_s0"] = "owner_more_bldg_damage", ["stina_s0"] = "owner_stina",
        ["schliemann_s0"] = "owner_schliemann", ["livingstone_s0"] = "owner_livingstone",
    };
    // the aura sources of effects.js: what their owner buff is
    static readonly Dictionary<string, string> AURA_OWNER = new()
    {
        ["hu_warrior"] = "owner_more_damage", ["aje_warrior"] = "owner_less_damage", ["mayor_s0"] = "owner_less_damage", ["hu_magic_cauldron"] = "owner_more_damage",
        ["ninigi_cauldron"] = "owner_fire_arrows", ["ninigi_smoke_tower"] = "owner_invisibility_bldg",
        ["ninigi_smokebomb_thrower"] = "owner_invisibility_nobldg", ["ninigi_parasaurolophus_drums"] = "owner_more_damage", ["hu_scout"] = "owner_more_damage", ["hu_rhino"] = "owner_more_defense",
    };
    // buildings that mark themselves while they stand (Building.usl: CTemple 2795, CHarbour 5175, CPoisonDung 4526,
    // CQuicksand 4746, CSnareTrap 4964); the scarecrow and the skull protector have none (Building.usl:6112 is commented out)
    static readonly Dictionary<string, string> BLDG_OWNER = new()
    {
        ["CTemple"] = "owner_healing_temple", ["CHarbour"] = "owner_healing_harbour", ["CPoisonDung"] = "owner_poison", ["CQuicksand"] = "owner_hold_units", ["CSnareTrap"] = "owner_hold_units",
        ["CHuMudPit"] = "owner_less_attack_speed",          // MIRAGE Building.usl CHuMudPit.OnInit AddRangedBuff
    };
    /// <summary>a building's own mark: its class's, else an ancestor's (MIRAGE's Amazon temple is a CTemple)</summary>
    static string? BldgOwner(Rules D, string name, string? sc)
    {
        if (sc != null && BLDG_OWNER.TryGetValue(sc, out var bo)) return bo;
        foreach (var (k, v) in BLDG_OWNER) if (D.IsA(name, k)) return v;
        return null;
    }
    // camouflage layers that are the buff "is_camouflaged" (FO:1731/1738 ninja, FO:1878 Aje shaman, Entrench.usl:88) -
    // not Livingstone's vanishing nor a hidden trap
    static readonly HashSet<string> CAMO_BUFF = new() { "entr", "disg", "aje" };

    public static List<string> BuffsOf(World.World W, Entity? e)
    {
        var res = new List<string>();
        if (e == null || !e.Alive) return res;
        double t = W.Time;
        var a = e.St.Aura; var b = e.St.Bonus;
        var k = (W.Data.Kind(e.Name) ?? e.Name).ToLowerInvariant();
        // the sources
        // a hero marks itself once one of its special abilities is switched on (Hero.usl:282-321: any "special_abilities"
        // change - RangeEffect, self_heal, ranged_heal ...; never taken away)
        if (e is Unit && HERO_OWNER.TryGetValue(k, out var ho) && e.Stats != null && e.Stats.Abilities.Values.Any(x => x.On("enabled"))) res.Add(ho);
        if (k == "darwin_s0") res.Add("no_animal_aggro");                  // (he gives it to himself at his start, Hero.usl:61)
        if (e.Owner != null && W.AuraOn(e)) res.Add(AURA_OWNER.TryGetValue(k, out var ao) ? ao : "owner_more_damage");
        if (e is Building bb && bb.Built && e.Owner != null && BldgOwner(W.Data, e.Name, W.Data.Script(e.Name)) is string bo) res.Add(bo);
        // what acts on it
        var w = e.Weapon ?? e.Weapons.Long;
        var cs = w != null ? e.Cs(w) : null;
        if (cs != null && cs.Dmg > 0) { double d = W.AttackBoni(e, cs.Dmg, w); if (d > cs.Dmg + 1e-6) res.Add("more_damage"); else if (d < cs.Dmg - 1e-6) res.Add("less_damage"); }
        double def = W.TempDef(e, 0);                    // (standing on a wall gives no buff: AddTemporaryRangedDefenseBoni, FO:7952)
        if (def > 0) res.Add("more_defense"); else if (def < 0) res.Add("less_defense");
        if (a.Slowhand > 1) res.Add("less_attack_speed");      // Ada's aura (EFFECT_ADA_SLOWHAND, FO:1826)
        double rg = W.RangeBonus(e);
        if (rg > 0) res.Add("more_range"); else if (rg < 0) res.Add("less_range");
        if (b.BLDGDAMAGE > 0) res.Add("more_bldg_damage");
        if (a.NoAnimalAggro) res.Add("no_animal_aggro");
        if (a.DrainBy != null && a.DrainBy.Count > 0) res.Add("drain_life");
        if (e.St.Poison != null || e.St.Poison2 != null) res.Add("is_poisoned");
        W.SeasBuffs(e, res);                                 // (MIRAGE's psi disruptor and speaker: MirageSeasBuildings.cs)
        if (e.St.Active("trapped", t)) res.Add("is_held");
        if (e.St.Camo.Any(l => CAMO_BUFF.Contains(l))) res.Add("is_camouflaged");
        if (a.Invisible) res.Add("is_invisible");
        if (e is Unit u)
        {
            if (!double.IsNaN(u.HealedT) && t - u.HealedT < 2.5) res.Add("is_getting_healed");
            if (u.FasterBuild) res.Add("faster_buildup");
            if (u.SqTranq > t) res.Add("sq_tranquilized");                   // the sequel's tranquilizer darts (sequel/runtime.js)
        }
        return res;
    }

    /// <summary>the buffs of the table in its order that a viewer sees on e (Info.usl: own objects all of them; else by the
    /// buff's visibility 1 all, 2 enemies, 3 allies)</summary>
    public static List<BuffDef> VisibleBuffs(World.World W, Entity e, Player? viewer)
    {
        var table = W.Data.Extra.Buffs;
        var have = new HashSet<string>(BuffsOf(W, e));
        bool own = e.Owner == viewer;
        int rel = e.Owner != null && viewer != null && !own ? viewer.Relation(e.Owner) : -1;
        return table.Where(x => have.Contains(x.Attrib) && (own || x.Vis == 1 || (x.Vis == 2 && rel == 0) || (x.Vis == 3 && rel == 2))).ToList();
    }
}
