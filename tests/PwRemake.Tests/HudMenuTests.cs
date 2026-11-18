// The command bar's menu on selection (user report: "whenever I select a unit or building that has some kind of action or
// production, that menu should always be open by default, and now it feels mostly random"): HudModel.CommandBar opens
// the selection's main menu - Build, Produce or Special, the first it has - every time the player selects, by any way,
// the same selection again too; a menu closed by hand stays closed until the selection changes; a site opens Produce when
// it is finished; deaths leave the menu as it is; an ally's selection has none.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;

namespace PwRemake.Tests;

[TestClass]
public static class HudMenuTests
{
    sealed class Host : IHudContext
    {
        public Host(World w, Player me) { World = w; Me = me; }
        public World World { get; }
        public Player Me { get; }
        public bool Campaign => false;
        public bool Multiplayer => false;
        public void Submit(Command c) { }
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

    public static void OpensOnEverySelection(TestContext t)
    {
        var (R, M) = Sys1.LoadBare(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 3);
        var me = Sys1.AddPlayer(W, R, 0, "Hu", 1); var ally = Sys1.AddPlayer(W, R, 1, "Hu", 1);
        var worker = W.SpawnUnit("hu_worker", me, 10, 10)!;
        var warrior = W.SpawnUnit("hu_warrior", me, 14, 10)!;
        var hq = W.PlaceBuilding("hu_fireplace", me, 40, 40)!;
        var site = W.PlaceBuilding("hu_fireplace", me, -40, -40, 0, false)!;
        var allyHq = W.PlaceBuilding("hu_fireplace", ally, 40, -40)!;
        t.True(hq is { Built: true } && site is { Built: false }, "the world");
        var H = new Host(W, me); var m = new HudModel(H);

        // the session: the selection's event, then the command bar's refresh (Hud.SelectionChanged, Hud._Process)
        string? Select(bool silent, params Entity[] l) { H.Sel = l.ToList(); m.SelectionChanged(silent); m.CommandBar(); return m.Menu; }

        t.Equal("build", Select(false, worker), "a worker: Build");
        t.Equal("produce", Select(false, hq), "the headquarters: Produce");
        t.Equal("build", Select(false, worker, warrior), "a worker with a warrior (box): Build");
        t.Equal("build", Select(false, warrior, worker), "the same, the warrior first");
        t.Equal("produce", Select(false, hq), "Produce again");

        // closed by hand: closed while the selection stays; open again when the player selects - the same one too
        m.ToggleMenu("produce"); m.CommandBar();
        t.Equal(null, m.Menu, "closed by hand");
        m.CommandBar(); m.CommandBar();
        t.Equal(null, m.Menu, "stays closed while nothing changes");
        t.Equal("produce", Select(false, hq), "the same building clicked again: open");
        t.Equal(null, Select(true), "nothing selected (a click on the ground)");
        t.Equal("produce", Select(false, hq), "selected again after that: open");
        H.Sel = new List<Entity> { hq }; m.CommandBar();
        t.Equal("produce", m.Menu, "without the event (a game loaded): open too");

        // another menu opened by hand stays while the selection does; a new selection opens its main menu
        Select(false, worker, warrior); m.ToggleMenu("stances"); m.CommandBar();
        t.Equal("stances", m.Menu, "Stances by hand");
        t.Equal("build", Select(false, worker), "a new selection: Build, not the old Stances");

        // deaths (silent, the selection only shrinks) leave a closed menu closed
        Select(false, worker, warrior); m.ToggleMenu("build"); m.CommandBar();
        t.Equal(null, Select(true, worker), "the warrior died: still closed");

        // a site: no menu yet; Produce when it is finished - unless the player opened another one meanwhile
        t.Equal(null, Select(false, site), "a site: nothing to open yet");
        site.Built = true; m.CommandBar();
        t.Equal("produce", m.Menu, "the site finished: Produce");

        // an ally's building: no menu; then one's own again
        t.Equal(null, Select(false, allyHq), "an ally's building: none");
        t.Equal("produce", Select(false, hq), "one's own again: Produce");

        // the keyboard's row right after a new selection: its menu, not the default
        H.Sel = new List<Entity> { worker, warrior }; m.SelectionChanged(false);
        m.ArmRow("stances", 0);
        t.Equal("stances", m.Menu, "a key's menu wins over the new selection's");
        m.CommandBar();
        t.Equal("stances", m.Menu, "and stays");

        // the option off: nothing opens by itself
        m.AutoMenu = false; m.ToggleMenu("stances");
        t.Equal(null, Select(false, hq), "AutoMenu off: closed");
        t.Equal(null, Select(false, worker), "AutoMenu off: closed");
    }
}
