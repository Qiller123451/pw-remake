// The debug tools of a game with debug options (ui/menu.js debugTools; docs/DEBUG.md): +1000 food, wood, stone, +500
// skulls, the next epoch, free and instant on / off, heal everything, the selected units level up / are destroyed, put
// down an object (any class of the rules, yours / enemy / wild, level 1..5) at the middle of the screen, the fog of
// war on / off, win / lose the mission, mark an objective as done, destroy a player's units and buildings. The window
// stays open; a line says what was done. What changes the world - the fog of war switch too - is a command
// (src/Sim/Game/DebugToolCommand.cs) applied at the next step like the player's orders, so a saved game replays it.
// Ported from remake/src/ui/menu.js (debugTools, objectivesGroup) and remake/src/game/cheats.js (spawnable,
// spawnObject) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Game;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.UI.Game;

/// <summary>The debug tools' window (in the pause menu).</summary>
public sealed class DebugTools
{
    readonly GameSession S;
    readonly PauseMenu M;
    Label _did = null!;
    static string _lastSpawn = "";

    public DebugTools(GameSession s, PauseMenu m) { S = s; M = m; }

    void Say(string t) { if (GodotObject.IsInstanceValid(_did)) _did.Text = t; }

    void Run(DebugToolCommand c, bool reopen = false)
    {
        S.Submit(c, why =>
        {
            if (reopen) Show();
            Say(why == null ? c.Said : S.Reason(why));
        });
        // (the game stands still while the menu is open: the tool is applied now, as at the next step)
        if (S.MenuOpen) S.StepWhilePaused();
    }

    public void Show()
    {
        var W = S.World; var me = S.Me;
        var w = M.Open("Debug tools", 640);
        var b = w.Body;
        HBoxContainer Row() { var r = new HBoxContainer(); b.AddChild(r); return r; }
        Button Small(string t, Action a) => PwTheme.Btn(t, a, 150);
        b.AddChild(PwTheme.Heading("Resources", 15));
        var r1 = Row();
        r1.AddChild(Small("+1000 food, wood, stone", () => Run(new DebugToolCommand { Tool = "res" })));
        r1.AddChild(Small("+500 skulls", () => Run(new DebugToolCommand { Tool = "skulls" })));
        b.AddChild(PwTheme.Heading("You", 15));
        var r2 = Row();
        r2.AddChild(Small("Next epoch", () => Run(new DebugToolCommand { Tool = "epoch" })));
        r2.AddChild(Small(me.Debug ? "Free and instant: on" : "Free and instant: off", () => Run(new DebugToolCommand { Tool = "free" }, true)));
        r2.AddChild(Small("Heal everything", () => Run(new DebugToolCommand { Tool = "heal" })));
        var r3 = Row();
        r3.AddChild(Small("Selected: level up", () => Run(new DebugToolCommand { Tool = "lvl", Entities = S.Selection.Items.Where(x => x.Alive && x.Owner == me && x is Unit).Select(x => x.Id).ToArray() })));
        r3.AddChild(Small("Selected: destroy", () => Run(new DebugToolCommand { Tool = "kill", Entities = S.Selection.Items.Where(x => x.Alive).Select(x => x.Id).ToArray() })));
        // ---- put down an object
        b.AddChild(PwTheme.Heading("Put down an object", 15));
        var r4 = Row();
        var cls = new LineEdit { PlaceholderText = "class, e.g. hu_warrior", Text = _lastSpawn, CustomMinimumSize = new Vector2(220, 0) };
        var list = DebugToolCommand.Spawnable(S.Rules);
        var pick = new OptionButton { CustomMinimumSize = new Vector2(160, 0) };
        pick.AddItem("(list)");
        foreach (var (n, t, ty) in list) pick.AddItem($"{n}  ({t} {ty})");
        pick.ItemSelected += i => { if (i > 0) cls.Text = list[(int)i - 1].Name; };
        var side = new OptionButton(); side.AddItem("yours"); side.AddItem("enemy"); side.AddItem("wild");
        var lvl = new OptionButton(); for (int l = 1; l <= 5; l++) lvl.AddItem($"level {l}");
        r4.AddChild(cls); r4.AddChild(pick); r4.AddChild(side); r4.AddChild(lvl);
        r4.AddChild(Small("Put down", () =>
        {
            var n = cls.Text.Trim();
            if (n.Length == 0) { Say("Type the class name of the object."); return; }
            _lastSpawn = n;
            Run(new DebugToolCommand { Tool = "spawn", Class = n, Side = new[] { "me", "foe", "wild" }[side.Selected], Level = lvl.Selected + 1, X = S.Camera.X, Z = S.Camera.Z });
        }));
        b.AddChild(PwTheme.Para("At the middle of the screen.", 11));
        // ---- world
        b.AddChild(PwTheme.Heading("World", 15));
        var r5 = Row();
        r5.AddChild(Small(S.Fog.RevealAll ? "Fog of war: off" : "Fog of war: on", () => Run(new DebugToolCommand { Tool = "fog", On = !S.Fog.RevealAll }, true)));
        r5.AddChild(Small(S.Skirmish != null ? "Win the game" : "Win the mission", () => { Run(new DebugToolCommand { Tool = "win" }); S.CloseMenu(); }));
        r5.AddChild(Small(S.Skirmish != null ? "Lose the game" : "Lose the mission", () => { Run(new DebugToolCommand { Tool = "lose" }); S.CloseMenu(); }));
        // ---- objectives (the open ones of the mission), a player's objects
        var E = S.Mission?.Engine;
        if (E != null)
        {
            b.AddChild(PwTheme.Heading("Objectives", 15));
            var r6 = Row();
            var open = E.Quests.Where(q => q.Visible && !q.Accomplished && !q.Unaccomplishable).ToList();
            var obj = new OptionButton { CustomMinimumSize = new Vector2(380, 0) };
            if (open.Count == 0) obj.AddItem("(none open)");
            foreach (var q in open) obj.AddItem((q.Main ? "" : "(optional) ") + QuestTitle(q));
            r6.AddChild(obj);
            r6.AddChild(Small("Mark as done", () =>
            {
                if (open.Count == 0) { Say("No objective chosen."); return; }
                Run(new DebugToolCommand { Tool = "objdone", Text = open[obj.Selected].Name }, true);
            }));
        }
        // the remake's own campaigns: their objectives (docs/SEQUEL.md)
        if (S.Sequel != null)
        {
            b.AddChild(PwTheme.Heading("Objectives", 15));
            var r6 = Row();
            var open = S.Sequel.Runtime.Objectives.Where(o => !o.Done && !o.Failed).ToList();
            var obj = new OptionButton { CustomMinimumSize = new Vector2(380, 0) };
            if (open.Count == 0) obj.AddItem("(none open)");
            foreach (var o in open) obj.AddItem((o.Secondary ? "(optional) " : "") + o.Text);
            r6.AddChild(obj);
            r6.AddChild(Small("Mark as done", () =>
            {
                if (open.Count == 0) { Say("No objective chosen."); return; }
                Run(new DebugToolCommand { Tool = "objdone", Text = open[obj.Selected].Id }, true);
            }));
        }
        // a player's objects (a mission's objectives, a skirmish's opponents)
        {
            var r7 = Row();
            var others = W.Players.Where(p => p != me && !p.Defeated).ToList();
            var plr = new OptionButton { CustomMinimumSize = new Vector2(380, 0) };
            foreach (var p in others) plr.AddItem($"{PlayerName(p)} #{p.Id} ({(me.IsEnemy(p) ? "enemy" : me.IsFriend(p) ? "ally" : "neutral")})");
            r7.AddChild(plr);
            r7.AddChild(Small("Destroy all its units and buildings", () =>
            {
                if (others.Count == 0) { Say("No player chosen."); return; }
                Run(new DebugToolCommand { Tool = "wipe", Target = others[plr.Selected].Id });
            }));
            if (E != null) b.AddChild(PwTheme.Para("For objectives like \"destroy the base\": the mission sees them destroyed (walls, gates and the Scorpion stay).", 11));
        }
        _did = PwTheme.Para("", 13, PwTheme.Hi);
        b.AddChild(_did);
        var r8 = Row();
        r8.AddChild(Small("Back", M.Main));
        r8.AddChild(Small("Resume game", M.Close));
    }

    string QuestTitle(PwRemake.Sim.Scripting.QuestState q)
    {
        var t = S.Presenter.State.Data.Texts;
        return t.TryGetValue(q.Headline, out var h) && h.Length > 0 ? h : q.Headline.Length > 0 ? q.Headline : q.Name;
    }
    string PlayerName(Player p)
    {
        var n = p.Name ?? "";
        return S.Presenter.State.Data.Texts.TryGetValue(n, out var t) && t.Length > 0 ? t : Regex.Replace(n, @"^_PN_\w+?_", "") is { Length: > 0 } s ? s : "Player";
    }
}
