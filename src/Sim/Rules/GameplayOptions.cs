// MIRAGE's gameplay options that switch tech tree filters on for every player at the start of a game.
//
// Reference (MIRAGE master): Scripts/Server/classes/misc/StartLocation.usl MirageInitialized - InitializeEpochSix and
// InitializeRemoveTitans add /Filters/<tribe>/Upgrades/xenage/epoch_six_enabled and .../titans_disabled for all four
// tribes; Scripts/Server/misc/MirageSrvMgr.usl EpochSix() (the option "MaxEpoch" >= 6) and RemoveTitans();
// Scripts/Game/UI/Mirage_UI_scripts.usl SetDefaultStuff: the defaults MaxEpoch 6, RemoveTitans false, FlyingEnabled false;
// StartLocation.usl InitializeFlyingEnabled: without "FlyingEnabled" .../xenage/flying_disabled for all four tribes;
// InitWarpGate: with "Portals" (default off; only when the warp gate is off) <tribe>_portal_terminus/portal_terminus_visible.
// "BldgMaxCapacity" ("Building limit", default off: Mirage_UI_scripts.usl SetDefaultStuff GetSetB(..., false)) is no
// filter: it switches MIRAGE's building limits on (CMirageSrvMgr.BldgMaxCapacity: CBLDGMgr counts, CanAddBuilding;
// CommandBar.CheckBuildConditions) - World.BuildingLimits.
//
// Why it matters: MIRAGE's sixth stage (the headquarters / fireplace / resource collector upgrade "final_stage") has
// visibility -1 in the tech tree; age_5 adds 1 and epoch_six_enabled adds 1, so it is offered only with both. Without
// the option filter the remake never offered it, and everything final_stage unlocks (the SEAS great cannon and the
// Scorpion's launch pad, the Norsemen's colossus launch pad, the Dragon Clan's pirate boss and King Kong, the
// Dustriders' catapult rex ...) stayed hidden. titans_disabled ("no titans") hides and disables the titans
// (visibility -2, disabled 1, ai_cap 0).
// flying_disabled (MIRAGE's default: "FlyingEnabled" off) hides and disables the flyers' build actions (visibility -2,
// disabled 1, ai_cap 0): the Norsemen's and the Dustriders' pteranodons, the Dragon Clan's cargolifter and dirigible,
// the SEAS flying helicopter (its build action and the helicopter's upgrade to it), the Norsemen's jetpack move
// jetfly_on (-1, disabled); the zeppelins' flying_unit becomes false (CZeppelin flies only with the option: it walks
// and swims); the menu positions of the units after them move up (menupos -1 / -2).
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PwRemake.Sim.Rules;

/// <summary>MIRAGE's game options that are tech tree filters (Server/GameplayOptions). The base game has none of these
/// filters: <see cref="Filters"/> gives only those the tree has.</summary>
public sealed class GameplayOptions
{
    /// <summary>"MaxEpoch" 6: the sixth stage (final_stage) can be researched (MIRAGE's default)</summary>
    [JsonInclude, JsonPropertyName("epochSix")] public bool EpochSix = true;
    /// <summary>"RemoveTitans": no titans (MIRAGE's default: off)</summary>
    [JsonInclude, JsonPropertyName("removeTitans")] public bool RemoveTitans;
    /// <summary>"FlyingEnabled": flying units can be built (MIRAGE's default: off - the xenage/flying_disabled filters)</summary>
    [JsonInclude, JsonPropertyName("flyingEnabled")] public bool FlyingEnabled;
    /// <summary>"Portals": the portal termini can be built (MIRAGE's default: off; MIRAGE offers it only without the warp
    /// gate - StartLocation.usl InitWarpGate: <tribe>_portal_terminus/portal_terminus_visible)</summary>
    [JsonInclude, JsonPropertyName("portals")] public bool Portals;
    /// <summary>"BldgMaxCapacity" ("Building limit"): a player may have at most max_quantity buildings of a class
    /// (MIRAGE's tech tree; queued upgrades with check = 1 count - World.BuildingLimits). MIRAGE's default: off</summary>
    [JsonInclude, JsonPropertyName("buildingLimit")] public bool BuildingLimit;

    public static readonly GameplayOptions Default = new();

    static readonly string[] TRIBES = { "Aje", "Hu", "Ninigi", "SEAS" };

    /// <summary>the filters (tree paths without "/Filters/") these options switch on, as StartLocation.usl adds them:
    /// for all four tribes, whatever the player's tribe</summary>
    public IEnumerable<string> Filters(TechTreeBase tree)
    {
        foreach (var (on, name) in new[] { (EpochSix, "epoch_six_enabled"), (RemoveTitans, "titans_disabled"), (!FlyingEnabled, "flying_disabled") })
        {
            if (!on) continue;
            foreach (var t in TRIBES)
            {
                var f = $"{t}/Upgrades/xenage/{name}";
                if (tree.Filter(f) != null) yield return f;
            }
        }
        if (Portals)
            foreach (var t in TRIBES)
            {
                var f = $"{t}/Upgrades/{t.ToLowerInvariant()}_portal_terminus/portal_terminus_visible";
                if (tree.Filter(f) != null) yield return f;
            }
    }

    /// <summary>does the tree have building limits at all (MIRAGE: max_quantity and the Filters/&lt;tribe&gt;/BuildingCount
    /// filters; the base game none)? The menus show "Building limit" only then.</summary>
    public static bool HasBuildingLimits(TechTreeBase? tree)
    {
        if (tree == null) return false;
        foreach (var t in TRIBES) if (tree.Filter($"{t}/BuildingCount") != null) return true;
        return false;
    }

    public GameplayOptions Clone() => (GameplayOptions)MemberwiseClone();

    /// <summary>MIRAGE's defaults (null options are the same)</summary>
    [JsonIgnore] public bool IsDefault => EpochSix && !RemoveTitans && !FlyingEnabled && !Portals && !BuildingLimit;

    /// <summary>no option filter at all: the trees as the browser remake had them (its golden tests)</summary>
    public static GameplayOptions NoFilters => new() { EpochSix = false, RemoveTitans = false, FlyingEnabled = true };

    public override string ToString() => $"epochSix={(EpochSix ? "on" : "off")} removeTitans={(RemoveTitans ? "on" : "off")} flyingEnabled={(FlyingEnabled ? "on" : "off")} portals={(Portals ? "on" : "off")} buildingLimit={(BuildingLimit ? "on" : "off")}";
}
