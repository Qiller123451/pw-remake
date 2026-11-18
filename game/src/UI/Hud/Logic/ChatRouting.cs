// Which computer players read a chat line (no Godot types: tests/PwRemake.Tests links this folder).
//
// MIRAGE master (Scripts/Game/UI/ChatWindow.usl CIngameChatBar, since 2.6.8: "You can ask AI player for help by
// writing "help" or "Help" in the dm or allies chat", "AI players can read group chats from now on"): the chat bar's
// list holds To All, To Allies, To Enemies and every other player by name. A line to one player that is a computer
// player goes to it as its request ("AI<slot>: <line>": DirectMsg -> CEvt_AiGenericStringEvent; the AI answers help /
// food / wood / stone / gift - src/Sim/Ai/Chat.cs - and an enemy's request is refused); a line to the allies or the
// enemies goes to each player of that side the same way (GroupMsg -> DirectMsg), so every computer player among them
// reads it. A line to all reaches a computer player only with the prefix "AI<n>:" (the original's CW:147-153, kept).
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PwRemake.Sim.World;

namespace PwRemake.UI.Hud.Logic;

public static class ChatRouting
{
    static readonly Regex AiPrefix = new(@"^AI(\d{1,2})\s*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>the target of a direct line to player p (the chat bar's list entry)</summary>
    public static string To(Player p) => "p" + p.Id;

    /// <summary>the computer players the chat bar lists by name for a direct line: every other player that is
    /// a computer player and still in the game</summary>
    public static List<Player> DirectTargets(World W, Player me)
    {
        var l = new List<Player>();
        if (W.Ai == null) return l;
        foreach (var p in W.Players) if (p != me && !p.Defeated && W.Ai.Get(p.Id) != null) l.Add(p);
        return l;
    }

    /// <summary>the computer players that read a line sent to `to` ("all" | "allies" | "enemies" | "p&lt;id&gt;") and
    /// what they read (the request's text): (slot, words)</summary>
    public static List<(int Slot, string Word)> AiReaders(World W, Player me, string to, string text)
    {
        var res = new List<(int, string)>();
        text = (text ?? "").Trim();
        if (text == "" || W.Ai == null) return res;
        if (to == "all")
        {
            var m = AiPrefix.Match(text);
            if (m.Success) res.Add((int.Parse(m.Groups[1].Value), m.Groups[2].Value));
            return res;
        }
        if (to.Length > 1 && to[0] == 'p' && int.TryParse(to.AsSpan(1), out int id))
        {
            foreach (var p in W.Players) if (p.Id == id && p != me && W.Ai.Get(p.Id) != null) res.Add((p.Id, text));
            return res;
        }
        bool allies = to == "allies";
        if (!allies && to != "enemies") return res;
        foreach (var p in W.Players)
        {
            if (p == me || p.Defeated || W.Ai.Get(p.Id) == null) continue;
            if (allies ? me.IsFriend(p) : me.IsEnemy(p)) res.Add((p.Id, text));
        }
        return res;
    }
}
