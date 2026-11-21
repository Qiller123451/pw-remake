// Shift / Ctrl selection (Kacper: "when I hold Shift I should be able to select multiple buildings at once and I
// cannot"): Selection.Combine follows GameInputController.usl MT_MouseLBtnUp - Shift+click adds a building to selected
// buildings (never removes), Ctrl+click adds or removes, a box with Shift adds, with Ctrl toggles; then own over others,
// units over buildings over the rest. And the HUD of several buildings: the Produce menu of the first, a card goes to
// the least busy of them (CmdButton.usl -> CCommandBar.SortByQueue; Mirage.usl CGPCEmitter.SortByQueue).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;
using PwRemake.UI.PlayerInput;

namespace PwRemake.Tests;

[TestClass]
public static class ShiftSelectTests
{
    sealed class Host : IHudContext
    {
        public Host(World w, Player me) { World = w; Me = me; }
        public World World { get; }
        public Player Me { get; }
        public bool Campaign => false;
        public bool Multiplayer => false;
        public readonly List<Command> Sent = new();
        public void Submit(Command c) => Sent.Add(c);
        public List<Entity> Sel = new();
        public IReadOnlyList<Entity> Selection => Sel;
        public void SetSelection(IReadOnlyList<Entity> list, bool silent = false) => Sel = list.ToList();
        public (double X, double Z, double Yaw) Camera => (0, 0, 0);
        public void MoveCamera(double x, double z) { }
        public IReadOnlyList<(double X, double Z)>? ViewCorners() => null;
        public IFogOfWar? Fog => null;
        public int FogVersion => 0;
        public string? InputMode => null;
        public void SetInputMode(string? mode, Sim.Rules.ActionDef? a = null) { }
        public IReadOnlyList<Entity>? Group(int k) => null;
        public string? SelectedGroup => null;
        public void GroupButton(int k, string how) { }
        public void OrderOn(Entity target, bool shift, bool ctrl) { }
        public (double X, double Z) MapToWorld(double mx, double my) => (mx, my);
        public void UiSound(params string[] names) { }
        public void ErrorSound() { }
        public void Ordered(string type, IReadOnlyList<Entity> units) { }
        public void OpenMenu() { }
        public void OpenChat(bool history) { }
    }

    public static void ShiftAndCtrlSelectBuildings(TestContext t)
    {
        var (R, M) = Sys1.LoadBare(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 3);
        var me = Sys1.AddPlayer(W, R, 0, "Hu", 1); var foe = Sys1.AddPlayer(W, R, 1, "Hu", 2);
        var b1 = W.PlaceBuilding("hu_fireplace", me, 40, 40)!;
        var b2 = W.PlaceBuilding("hu_fireplace", me, -40, 40)!;
        var b3 = W.PlaceBuilding("hu_warehouse", me, 40, -40)!;
        var enemy = W.PlaceBuilding("hu_fireplace", foe, -40, -40)!;
        var worker = W.SpawnUnit("hu_worker", me, 10, 10)!;
        var warrior = W.SpawnUnit("hu_warrior", me, 14, 10)!;
        var sel = new Selection(() => me);
        string Ids() => string.Join(",", sel.Items.Select(e => e == b1 ? "b1" : e == b2 ? "b2" : e == b3 ? "b3" : e == enemy ? "enemy" : e == worker ? "worker" : e == warrior ? "warrior" : "?"));

        sel.Set(new[] { b1 });
        sel.Modify(new[] { b2 }, true, false);
        t.Equal("b1,b2", Ids(), "Shift+click: a second building added");
        sel.Modify(new[] { b3 }, true, false);
        t.Equal("b1,b2,b3", Ids(), "Shift+click: a building of another class too");
        sel.Modify(new[] { b2 }, true, false);
        t.Equal("b1,b2,b3", Ids(), "Shift+click on a selected one keeps it (Shift never removes)");
        sel.Modify(new[] { b2 }, false, true);
        t.Equal("b1,b3", Ids(), "Ctrl+click on a selected one takes it out");
        sel.Modify(new[] { b2 }, false, true);
        t.Equal("b1,b3,b2", Ids(), "Ctrl+click on another one adds it");
        sel.Modify(new[] { enemy }, true, false);
        t.Equal("b1,b3,b2", Ids(), "Shift+click on an enemy building: the own stay alone");
        sel.Modify(new[] { worker }, true, false);
        t.Equal("worker", Ids(), "Shift+click on a unit: units win over buildings");
        sel.Modify(new[] { b1 }, true, false);
        t.Equal("worker", Ids(), "Shift+click on a building with units selected: left out");

        // boxes: Shift - the boxed first, then the selection; Ctrl - each boxed one in or out
        sel.Set(new[] { b1 });
        sel.Modify(new Entity[] { b2, b3 }, true, false);
        t.Equal("b2,b3,b1", Ids(), "Shift+box of buildings: added");
        sel.Modify(new Entity[] { b1, b2 }, false, true);
        t.Equal("b3", Ids(), "Ctrl+box: the boxed selected ones out");
        sel.Modify(new Entity[] { b1, b2 }, false, true);
        t.Equal("b3,b1,b2", Ids(), "Ctrl+box: the boxed others in");
        sel.Modify(new Entity[] { worker, warrior }, true, false);
        t.Equal("worker,warrior", Ids(), "Shift+box of units over buildings: the units");
        t.Equal(2, Selection.Combine(new Entity[] { enemy }, new Entity[] { enemy, b1, b2 }, true, false, me).Count, "an enemy selected, own boxed: the own");
    }

    public static void SeveralBuildingsProduce(TestContext t)
    {
        var (R, M) = Sys1.LoadBare(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 3);
        var me = Sys1.AddPlayer(W, R, 0, "Hu", 1);
        var b1 = W.PlaceBuilding("hu_fireplace", me, 40, 40)!;
        var b2 = W.PlaceBuilding("hu_fireplace", me, -40, 40)!;
        var H = new Host(W, me); var m = new HudModel(H);
        H.Sel = new List<Entity> { b1, b2 };
        m.SelectionChanged(false);
        var bar = m.CommandBar();
        t.True(bar.Any(b => b.Id == "produce"), "two headquarters: Produce");
        t.Equal("produce", m.Menu, "Produce opens");
        var card = m.MenuRows().SelectMany(r => r.Cards).FirstOrDefault(c => c.A is { Kind: "Build" });
        t.True(card != null, "a unit card in the menu");
        if (card == null) return;

        // the first is busy: the card goes to the second; Shift's five spread over both, the least busy first
        b1.Queue.Add(new QueueItem { Action = card.A!, T = 1, Total = 10 });
        b1.Queue.Add(new QueueItem { Action = card.A!, T = 0, Total = 10 });
        m.G.UseAction(card, H.Sel, false);
        var q = H.Sent.OfType<QueueActionCommand>().ToList();
        t.True(q.Count == 1 && q[0].Producer == b2.Id && q[0].Count == 1, "one: to the idle one");
        H.Sent.Clear();
        m.G.UseAction(card, H.Sel, true);
        q = H.Sent.OfType<QueueActionCommand>().ToList();
        int To(Entity e) => q.Where(c => c.Producer == e.Id).Sum(c => c.Count);
        t.Equal(5, To(b1) + To(b2), "Shift: five");
        t.Equal(2, To(b1), "Shift: the busy one gets 2 (2 + 2 = 4 queued)");
        t.Equal(3, To(b2), "Shift: the idle one gets 3 (0 + 3 = 3 queued)");

        // one building: as before
        H.Sent.Clear(); H.Sel = new List<Entity> { b1 };
        m.G.UseAction(card, H.Sel, false);
        q = H.Sent.OfType<QueueActionCommand>().ToList();
        t.True(q.Count == 1 && q[0].Producer == b1.Id, "one building selected: to it");
    }
}
