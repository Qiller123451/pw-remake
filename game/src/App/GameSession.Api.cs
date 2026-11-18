// The game API of the input and the menus (main.js "game API used by the HUD / input"). The HUD's own part of it -
// producerActions, buildActions, moveActions, moveState, useAction, unqueue, changeLevel, levelUp, killSelected,
// select - is the HUD's HudGame (game/src/UI/Hud/Logic/HudGame.cs, on commands); the session uses that one
// (Game) and adds what only the input and the menus ask: idle workers, the game speed, the home object, the map ping,
// what the player sees.
// Ported from remake/src/main.js (pw-exporter 98c7703).
using System;
using System.Linq;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;

namespace PwRemake.App;

public partial class GameSession
{
    /// <summary>main.js's HUD API on commands (the HUD's: one implementation)</summary>
    public HudGame Game => HudView.Model.G;
    /// <summary>main.js G.reason: the text of a reason code</summary>
    public string Reason(string? why) => why == null ? "" : HudGame.Reason(why);

    static readonly System.Collections.Generic.HashSet<string> s_once = new();
    static void ErrOnce(string key, string msg, Exception e) { if (s_once.Add(key)) L.Error(msg, e); }

    /// <summary>rally points: only for what produces units and the moving harbours</summary>
    public bool CanRally(Entity? e) => Game.CanRally(e);
    /// <summary>Del: the first selected (Kill), Shift+Del: all of them (KillAll)</summary>
    public void KillSelected(int n = int.MaxValue) => Game.KillSelected(n);
    public void LevelUp(Unit u) => Game.LevelUp(u);

    int _idleIdx;
    /// <summary>how: "next" (the next one, the camera goes there), "add" (to the selection), "all" (every idle worker)</summary>
    public void SelectIdleWorker(string how = "next")
    {
        var idle = World.Units.Where(u => u.Alive && u.Owner == Me && u.IsWorker && u.Task.Type == "idle").ToList();
        if (idle.Count == 0) { Message("No idle workers"); return; }
        if (how == "all") { Selection.Set(idle); return; }
        _idleIdx = (_idleIdx + 1) % idle.Count;
        var u = idle[_idleIdx];
        if (how == "add") { Selection.Add(u); return; }
        Selection.Set(new[] { u }); CenterOn(u);
    }

    static readonly double[] SPEEDS = { 0.5, 1, 1.5, 2 };
    /// <summary>game speed by key (Keys.srf AccTime / DecTime / ResetTime): "+" | "-" | "*"</summary>
    public void SetSpeed(string key)
    {
        int i = Array.IndexOf(SPEEDS, GameApp.Settings.GameSpeed);
        double v = key == "*" ? 1 : SPEEDS[Math.Clamp((i < 0 ? 1 : i) + (key == "+" ? 1 : -1), 0, SPEEDS.Length - 1)];
        GameApp.Settings.GameSpeed = v;
        Message($"Game speed {(v == 1 ? "normal" : "×" + v)}");
    }

    public Entity? HomeEntity() => (Entity?)World.Buildings.FirstOrDefault(b => b.Alive && b.Owner == Me) ?? World.Units.FirstOrDefault(u => u.Alive && u.Owner == Me);

    /// <summary>the minimap's ping button (MiniMap2.usl CGameWrap.SendMapPing)</summary>
    public void MapPing(double x, double z) => HudView.MapPing(x, z);

    /// <summary>is the entity seen by the player (own / seen through, or visible in the fog)</summary>
    public bool VisibleToMe(Entity e) => Sees(e.Owner) || Fog.Visible(e.Pos.X, e.Pos.Z);
}
