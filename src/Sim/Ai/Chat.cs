// The computer player talking to the other players (docs/spec/ai_group6.md §5): the cry for help when its village
// is overrun (goals/AiGoalGuardVillage.usl:615-716) and the answers to "AI<n>: <word>" requests of the chat window
// and the diplomacy window (Modules/AiModuleControl.usl:614-731). Texts: newsticker _NT_AI_* (gamedata ui).
//
//   * CryForHelp: on skirmish maps, while defending, more than 3 attackers (no wild animal among them) and no more
//     defenders than attackers, not in "Single 15": "Help! My Base is attacked ..." to every friendly player, again
//     after 10 guard thinks (about 20-35 s).
//   * OnPlayerRequest(from, word): help - up to 4 / 6 / 8 pool units (Easy / Medium / Hard) attack the enemies near
//     the asker's village when the pool holds more than 3 and the village is not defending; food / wood / stone -
//     100 of it when it has that much; gift - thanks; an enemy asking - refused; iron (skulls): no answer (the
//     original has no branch); other words: the original's Eliza chatbot is engine code [?engine] - no answer.
//
// Ported from remake/src/game/ai/chat.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Ai;

public sealed partial class TribeAI
{
    static readonly Dictionary<string, int> SUPPORT = new() { ["Easy"] = 4, ["Medium"] = 6, ["Hard"] = 8 };
    static readonly Dictionary<string, string> RES_KEY = new() { ["food"] = "FOOD", ["wood"] = "WOOD", ["stone"] = "STON" };
    double _helpT;

    void Say(Player to, string key, object? arg = null, string text = "") =>
        W.Emit("msg", new Args { ["player"] = to, ["key"] = key, ["arg"] = arg, ["text"] = text, ["kind"] = "ai", ["from"] = P });
    void CryForHelp(Census S, List<Unit> threats, int defenders)
    {
        if (!Multimap || !Defending || LevelName == "Single 15") return;
        if (threats.Count <= 3 || defenders > threats.Count || threats.Any(u => u.Wild || u.Owner == null)) return;
        if (W.Time < _helpT) return;
        _helpT = W.Time + 10 * (10 + 1 + Math.Floor(R.Next() * (W.Players.Count + 1))) * 0.2;
        foreach (var o in W.Players) if (o != P && !o.Defeated && P.IsFriend(o)) Say(o, "_NT_AI_HELPBASEATTACKED", null, "Help! My Base is attacked by a superiority!");
        W.Emit("aiHelp", new Args { ["player"] = P, ["x"] = S.Home?.X ?? 0, ["z"] = S.Home?.Z ?? 0 });
    }
    /// <summary>a request of another player -> the reply key (also said to them); null: no answer</summary>
    public string? OnPlayerRequest(Player from, string word)
    {
        var p = P; var w = Regex.Split((word ?? "").Trim(), @"\s+")[0].ToLowerInvariant();
        string Reply(string key, object? arg = null) { Say(from!, key, arg); return key; }
        if (w == "gift") return Reply("_NT_AI_THANKSFORHELP");
        if (w == "iron" || w.Length == 0) return null;
        if (!(w is "help" or "food" or "wood" or "stone")) return null;          // (Eliza: not ported)
        if (from != null && p.IsEnemy(from)) return Reply("_NT_AI_DENIEHELPENEMY");
        if (RES_KEY.TryGetValue(w, out var rk))
        {
            double reserve = ResV(Reserve, w);
            if (p[w] - reserve < 100) return Reply("_NT_AI_CANTSEND" + rk);
            p.Res[w] = p[w] - 100;
            from!.Deliver(w, 100, W.Time);
            return Reply("_NT_AI_SEND" + rk, 100);
        }
        // help: a support squad for the enemies near the asker's village (InitiateCoopSupportAttack, FM:978-1059)
        var S = Census(true);
        var at = BaseOf(from!);
        if (at == null || Defending || S.Pool.Count <= 3) return Reply("_NT_AI_CANTHELP");
        var targets = new List<Entity>();
        foreach (var u in W.Units) if (u.Alive && !u.Inside && u.Owner != null && p.IsEnemy(u.Owner) && !u.Naval && DistP(u, at.Value) < 80) targets.Add(u);
        if (targets.Count == 0) return Reply("_NT_AI_CANTHELP");
        int sup = SUPPORT.TryGetValue(Cls, out var s) ? s : 6;
        var units = (QueryPool(S, 1, sup, null, true) ?? new List<Unit>()).Take(sup).ToList();
        if (units.Count == 0) return Reply("_NT_AI_CANTHELP");
        SupportAttack(S, units, targets);
        return Reply("_NT_AI_CONFIRMHELP", units.Count);
    }
    /// <summary>A co-op AI commander helps an ally whose base is attacked (CoopCommander, the remake's own - the original
    /// sent its support squad only when asked in the chat): the "help" answer without the chat, a support squad of up to
    /// 4 / 6 / 8 pool units against the enemies within `radius` of `at`. -> null: sent; else why not (busy: one support
    /// squad at a time)</summary>
    public string? SupportAlly(Pt at, double radius = 60)
    {
        var p = P;
        if (Attacks.Any(a => a.Type == "coop")) return "busy";
        var S = Census();
        if (Defending || S.Pool.Count <= 3) return "pool";
        var targets = new List<Entity>();
        foreach (var u in W.Units) if (u.Alive && !u.Inside && !u.Parked && u.Owner != null && p.IsEnemy(u.Owner) && !u.Wild && !u.Naval && DistP(u, at) < radius) targets.Add(u);
        if (targets.Count == 0) return "none";
        int sup = SUPPORT.TryGetValue(Cls, out var s) ? s : 6;
        var units = (QueryPool(S, 1, sup, null, true) ?? new List<Unit>()).Take(sup).ToList();
        if (units.Count == 0) return "pool";
        SupportAttack(S, units, targets);
        return null;
    }
    /// <summary>the CoopSupportAttack goal: not one of the plan's three attacks (priority 10^6)</summary>
    Attack SupportAttack(Census S, List<Unit> units, List<Entity> targets)
    {
        var a = new Attack(this, "coop", "quick", targets: targets, hunt: true);
        Attacks.Add(a);
        Launched.Add(new LaunchLog { Id = a.Id, T = Math.Floor(W.Time + 0.5), Type = "coop", Goal = "quick", Scripted = false, Hunt = true, N = units.Count, Go = Math.Floor(W.Time + 0.5) });
        a.Add(units);
        a.StartCount = units.Count;
        AttackGo(S, a, a.Alive());
        return a;
    }
}
