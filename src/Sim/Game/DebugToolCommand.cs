// The debug tools of a game with debug options (ui/menu.js debugTools; docs/DEBUG.md) as a command of the human
// player, applied in the step like an order: +1000 food, wood, stone, +500 skulls, the next epoch, free and instant on /
// off, heal everything, the selected units level up / are destroyed, put down an object (any class of the rules, yours /
// enemy / wild, level 1..5), the fog of war on / off, win / lose the mission (a skirmish: the game), mark an objective as done, destroy a
// player's units and buildings. Everything it changes is the world's, so a saved game replays it (docs/SAVES.md): it
// was in the game's interface code with the mission's end and the quest mark as callbacks of the window, and the fog
// switch changed the view's grid directly.
// Ported from remake/src/ui/menu.js (debugTools, objectivesGroup) and remake/src/game/cheats.js (spawnable,
// spawnObject) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PwRemake.Sim.Commands;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Game;

/// <summary>A debug tool as a command of the human player (applied in the step like an order).</summary>
public sealed class DebugToolCommand : Command
{
    /// <summary>res | skulls | epoch | free | heal | lvl | kill | spawn | wipe | win | lose | objdone | fog</summary>
    public string Tool = "";
    public int[] Entities = Array.Empty<int>();
    public string Class = "", Side = "me";
    public int Level = 1, Target;
    public double X, Z;
    /// <summary>objdone: the quest's name or guid</summary>
    public string Text = "";
    /// <summary>fog: true = the whole map visible</summary>
    public bool On;
    /// <summary>what the tool did (the window's line; not saved)</summary>
    [JsonIgnore] public string Said = "";

    public override string? Apply(W_ W)
    {
        var me = W.Players.FirstOrDefault(p => p.Id == Player);
        if (me == null) return "none";
        IEnumerable<Entity> Mine() => W.Units.Cast<Entity>().Concat(W.Buildings).Where(e => e.Alive && e.Owner == me).ToList();
        var C = W.Campaign as Campaign.Campaign;
        switch (Tool)
        {
            case "res": foreach (var r in new[] { "food", "wood", "stone" }) me.Res[r] = me[r] + 1000; Said = "+1000 food, wood and stone (above the storage limit too)."; break;
            case "skulls": me.Res["skulls"] = me["skulls"] + 500; Said = "+500 skulls."; break;
            case "epoch":
                {
                    int e = me.Epoch();
                    Cheats.RaiseEpoch(W, me, e + 1, me.MaxEpoch);
                    Said = me.Epoch() > e ? $"Epoch {me.Epoch()}." : "No further epoch (the highest, or the limit of the debug options).";
                    break;
                }
            case "free": me.Debug = !me.Debug; Said = me.Debug ? "Everything is free and instant for you now." : "Costs and times are back."; break;
            case "heal":
                {
                    var l = Mine().Where(e => e.Hp < e.MaxHp && (e is Unit || e is Building { Built: true })).ToList();
                    foreach (var e in l) e.Hp = e.MaxHp;
                    Said = $"{l.Count} healed or repaired.";
                    break;
                }
            case "lvl":
                {
                    int n = 0;
                    foreach (var id in Entities) if (W.ById(id) is Unit u && u.Alive && u.Owner == me && W.LevelUp(u) == null) n++;
                    Said = $"{n} of the selected units levelled up{(me.Debug ? "" : " (for skulls)")}.";
                    break;
                }
            case "kill":
                {
                    int n = 0;
                    foreach (var id in Entities) if (W.ById(id) is { Alive: true } e) { W.Damage(e, e.Hp + 1e6, null); n++; }
                    Said = $"{n} destroyed.";
                    break;
                }
            case "spawn": { var got = SpawnObject(W, me, Class, Side, X, Z, Level); Said = got is string s ? s : $"{W.Data.Text(Class, ((Entity)got).Owner).Name} put down."; break; }
            case "wipe":
                {
                    var p = W.Players.FirstOrDefault(x => x.Id == Target);
                    if (p == null) { Said = "No player chosen."; break; }
                    int n = 0;
                    foreach (var e in W.Units.Cast<Entity>().Concat(W.Buildings).ToList())
                    {
                        if (!e.Alive || e.Owner != p || (e.Def != null && (e.Def.WallKind != null || e.Def.Gate || Regex.IsMatch(e.Def.Script ?? "", "^CScorpion")))) continue;
                        W.Damage(e, e.Hp + 1e7, null); n++;
                    }
                    Said = $"{n} objects of player #{Target} destroyed.";
                    break;
                }
            // (a skirmish: the game-over rules decide - the player has won or is out; gameover.js resolves it in its next
            // update, once)
            case "win": if (C != null) C.Host?.EndMission(true, "Won with the debug tools."); else if (W.Campaign is Sequel.SequelRuntime sw) sw.Win("Won with the debug tools."); else if (W.Go != null) me.Won = true; break;
            case "lose": if (C != null) C.Host?.EndMission(false, "Lost with the debug tools."); else if (W.Campaign is Sequel.SequelRuntime sl) sl.Lose("Lost with the debug tools."); else if (W.Go != null) me.Defeated = true; break;
            case "objdone":
                if (W.Campaign is Sequel.SequelRuntime so)
                {
                    // the remake's own campaigns: the objective of that id (else the first open one) done
                    var o = so.Objectives.FirstOrDefault(x => x.Id == Text) ?? so.Objectives.FirstOrDefault(x => !x.Done && !x.Failed);
                    if (o == null) { Said = "No open objective."; break; }
                    if (o.Of > 0) so.Progress(o.Id, o.Of); else so.Complete(o.Id);
                    Said = $"Objective done: {o.Text}";
                    break;
                }
                Said = MarkQuest(C, Text); break;
            case "fog":
                if (W.Fog == null) { Said = "There is no fog of war in this game."; break; }
                W.SetFogRevealAll(On);
                Said = On ? "The whole map is visible." : "The fog of war is back.";
                break;
            default: return "none";
        }
        return null;
    }

    // "Mark as done": the quest accomplished (TriggerEngine.SetQuest). The JS's engine also ran the triggers that would
    // have accomplished it (engine.js skipQuest) - not in the C# engine yet: only the mark changes
    static string MarkQuest(Campaign.Campaign? C, string name)
    {
        var E = C?.Engine;
        var q = E?.Quests.FirstOrDefault(x => x.Name == name || x.Guid == name);
        if (E == null || q == null) return "No objective chosen.";
        E.SetQuest(q, 1);
        return "Marked as done (only the mark changed: the C# trigger engine has no skipQuest yet).";
    }

    /// <summary>the classes that can be put down: units, animals and buildings of the tech tree (cheats.js spawnable)</summary>
    public static List<(string Name, string Tribe, string Type)> Spawnable(PwRemake.Sim.Rules.Rules D) =>
        D.Index.Where(kv => kv.Value.Type is "CHTR" or "ANML" or "VHCL" or "SHIP" or "BLDG").Select(kv => (kv.Key, kv.Value.Tribe, kv.Value.Type)).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();

    /// <summary>puts an object down at (x, z) for side "me" | "foe" | "wild": a unit, an animal, a finished building
    /// (cheats.js spawnObject) -> the object, or why not</summary>
    public static object SpawnObject(W_ W, Player me, string name, string side, double x, double z, int level)
    {
        var D = W.Data;
        // MIRAGE's Stina is hired riding her sabre-tooth (Building.usl: Stina_s0 -> special_eusmilus)
        if (Regex.IsMatch(name, "^stina_s0$", RegexOptions.IgnoreCase) && D.Extra.Mod == "MIRAGE" && D.Info("special_eusmilus") != null) name = "special_eusmilus";
        var ix = D.Info(name);
        if (ix == null) return $"{name}: no such object in this game's data";
        // side: me | foe (the first enemy) | wild | p<id> (that player's: colour checks of many players)
        Player? owner = side == "wild" ? null : side == "foe" ? W.Players.FirstOrDefault(p => p != me && (me.IsEnemy(p) || p.IsEnemy(me)))
            : side.Length > 1 && side[0] == 'p' && int.TryParse(side.AsSpan(1), out int pid) ? W.Players.FirstOrDefault(p => p.Id == pid) : me;
        if (side != "wild" && owner == null) return side == "foe" ? "no enemy player in this game" : $"no player {side}";
        if (ix.Type == "BLDG")
        {
            var at = W.PlacementOf(name, x, z, 0, owner);
            if (!at.Ok) return $"{name}: no room here";
            return (object?)W.PlaceBuilding(name, owner, at.X, at.Z, at.Rot, true) ?? $"{name} could not be placed";
        }
        int lv = Math.Max(D.MinLevel(name, owner), Math.Min(5, level));
        var u = W.SpawnUnit(name, owner, x, z, owner != null ? lv : null, W.Rng.Next() * 6.28);
        if (u == null) return $"{name} could not be placed (no model, or blocked ground)";
        if (owner == null) { u.HomeX = x; u.HomeZ = z; }
        if (owner != null) W.RecomputeCapsOf(owner);
        return u;
    }
}
