// MIRAGE's building limits: with the game option "Building limit" (BldgMaxCapacity) a player may have at most
// `max_quantity` buildings of a class (the tech tree's Objects/<tribe>/BLDG/<class>/max_quantity: towers 10, temples
// 5, walls 300, the Tesla machine 4 ...). The base game's tree has no max_quantity: nothing is limited there.
//
// Reference (MIRAGE master a349cb41 + the fix delivered to the modder, mirage_aje_farm_limits_v3):
//   Server/misc/BLDGConditionsMgr.usl CBLDGMgr  AddClass / RemoveClass (the counter per player and class; the filter
//       Filters/<tribe>/BuildingCount/<class> "max_reached" when the count reaches the limit), CanAddBuilding (count +
//       registered + 1 <= max), GetLimitBase / GetMaxQuantity (v2: the Aje farm types, max_quantity_<type>),
//       GetPlacementLimitClass (v2: a farm placed by the action aje_medium_farm / aje_huge_farm counts as that type)
//   Server/classes/buildings/Building.usl  OnInit (a building counts from the moment it is placed - a site too - under
//       GetLimitClass(): an Aje building's object flag, else its class), Die / DieFastAndSilent / Delete / SetOwner
//       (it stops counting, or counts for the new owner), GetCountedLimitClass / UpdateCountedLimitClass /
//       SetCountedLimitClass / SetPlacedLimitClass (v2: the class it was really counted under)
//   Server/classes/task/Action.usl CAction  Init (an upgrade with "check = '1'" - RequirementsMgr.CanBuildingBeAdded:
//       product_name / product_tribe - is refused at the limit and counts toward its product from the moment it is
//       queued; v3: for the owner of the building, not player 0), DeliverProduct (done: the old class is uncounted),
//       Break / EndTask (cancelled: the product is uncounted), Save / Load (v3: counted once, again after loading)
//   Server/ServerApp.usl PlaceObj  (a new building: CanAddBuilding of its placement class)
//   Game/UI/CommandBarEx/CommandBar.usl CheckBuildConditions  (the client: a Build/BLDG or Upgrades button is greyed
//       when GetBuildingCount + GetQueuedUpgradeCount (v3) >= the limit, or the max_reached filter is on); CmdButton.usl
//       Validate (the tooltip: _MI_BuildingMaxReached, "_MI_BuildingQuantity" n/max)
//
// The remake counts live instead of keeping counters: a player's count of a class = its alive buildings (sites
// included) whose limit class it is + the queued and running upgrades with check = 1 whose product it is, in the
// player's own buildings. That is exactly what the server's counters hold when they are right (CBLDGMgr after the v2/v3
// fixes): a building whose upgrade waits counts under its old class and the upgrade under the new one; done, the
// building has the new class and the queue entry is gone; cancelled, the entry is gone. Nothing is counted twice, a
// save cannot change it (saves replay the game) and a captured building counts for its new owner at once. The client
// counts the same way (v3's GetQueuedUpgradeCount), so the HUD and the server never disagree.
//
// Not a JS module (the browser remake had no building limits). docs/SIMULATION.md "Building limits".
using System;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public sealed partial class World
{
    /// <summary>the reason code of an action refused at its building limit</summary>
    public const string LIMIT = "limit";

    /// <summary>MIRAGE's game option "Building limit" for this player (CMirageSrvMgr.BldgMaxCapacity)</summary>
    public static bool BuildingLimitsOn(Player? p) => p != null && (p.Gameplay ?? GameplayOptions.Default).BuildingLimit;

    /// <summary>CBLDGMgr.GetLimitBase: the class whose tech tree node holds a limit class's limit (the Aje farm types are
    /// one class, aje_small_farm)</summary>
    public static string LimitBase(string limitClass) => limitClass is "aje_medium_farm" or "aje_huge_farm" ? "aje_small_farm" : limitClass;

    /// <summary>CBLDGMgr.GetMaxQuantity: the limit of a limit class in the player's tree; -1 = none. The farm types:
    /// max_quantity_&lt;type&gt; of aje_small_farm, else its max_quantity (each type separately)</summary>
    public int MaxQuantity(Player p, string limitClass)
    {
        var bas = LimitBase(limitClass);
        var n = Data.ObjectNode(bas, p);
        if (n == null || !n.IsObject) return -1;
        if (bas != limitClass || limitClass == "aje_small_farm")
        {
            var v = n["max_quantity_" + limitClass]?.Scalar;
            if (!string.IsNullOrEmpty(v)) return (int)TT.Num(v, -1);
        }
        var m = n["max_quantity"]?.Scalar;
        return string.IsNullOrEmpty(m) ? -1 : (int)TT.Num(m, -1);
    }

    /// <summary>the class a building counts under (Building.usl GetLimitClass / GetCountedLimitClass): an Aje building's
    /// object flag (the first of its tree node's "flags": an upgraded tower aje_medium_tower / aje_tesla_tower, a farm
    /// its type); a farm without one yet (a site) the type it was placed as (SetPlacedLimitClass: the build action
    /// aje_medium_farm / aje_huge_farm); else its class</summary>
    public string LimitClassOf(Building b)
    {
        if (b.Name.StartsWith("aje_", StringComparison.Ordinal))
        {
            var fl = Data.ObjectNode(b.Name, b.RulesOwner())?["flags"];
            if (fl != null && fl.IsObject) foreach (var k in fl.Keys) if (k != "_value") return k;
            if (b.Action is { Kind: "Build" } a && a.Id != b.Name && LimitBase(a.Id) == b.Name) return a.Id;
        }
        return b.Name;
    }

    /// <summary>the limit class an action adds a building of (null: none it counts): a Build/BLDG action its object
    /// (GetPlacementLimitClass: the farm type of aje_medium_farm / aje_huge_farm); an upgrade with check = 1 its product
    /// (product_name, else its id - CanBuildingBeAdded)</summary>
    public static string? LimitProductOf(ActionDef a)
    {
        if (a.Kind == "Build" && a.Cat == "Build/BLDG" && a.Results.Count > 0)
        {
            var obj = a.Results[0].Obj;
            return a.Id != obj && LimitBase(a.Id) == obj ? a.Id : obj;
        }
        if (a.Kind == "Upgrades" && a.LimitCheck) return a.Product ?? a.Id;
        return null;
    }

    /// <summary>how many of a limit class the player has: its alive buildings of that class (sites included) and its
    /// queued or running upgrades with check = 1 that make one (CBLDGMgr's count; CommandBar GetBuildingCount +
    /// GetQueuedUpgradeCount)</summary>
    public int LimitCount(Player p, string limitClass)
    {
        int n = 0;
        foreach (var b in Buildings)
        {
            if (!b.Alive || b.Owner != p) continue;
            if (LimitClassOf(b) == limitClass) n++;
            foreach (var q in b.Queue) if (q.Action.Kind == "Upgrades" && q.Action.LimitCheck && (q.Action.Product ?? q.Action.Id) == limitClass) n++;
        }
        return n;
    }

    /// <summary>the count and limit an action is held to (null: the option is off, or the action makes nothing limited)</summary>
    public (int Cur, int Max)? LimitOf(Player? p, ActionDef a)
    {
        if (p == null || !BuildingLimitsOn(p)) return null;
        var cls = LimitProductOf(a);
        if (cls == null) return null;
        int max = MaxQuantity(p, cls);
        if (max < 0) return null;
        return (LimitCount(p, cls), max);
    }

    /// <summary>"limit" when the action would add a building over its limit (CBLDGMgr.CanAddBuilding: count + 1 &lt;= max),
    /// else null</summary>
    public string? LimitWhy(Player? p, ActionDef a) => LimitOf(p, a) is var (cur, max) && cur >= max ? LIMIT : null;

    /// <summary>how many more of the action's product the player may have (int.MaxValue: no limit)</summary>
    public int LimitRoom(Player? p, ActionDef a) => LimitOf(p, a) is var (cur, max) ? Math.Max(0, max - cur) : int.MaxValue;
}
