// The game API the HUD (and the input) call: what a selection offers and what a click on a button does. The JS
// remake kept it on the global game object (main.js "game API used by the HUD / input": G.producerActions,
// G.buildActions, G.moveActions, G.moveState, G.useAction, G.unqueue, G.changeLevel, G.levelUp, G.killSelected,
// G.select ...). Orders became commands (src/Sim/Commands) submitted through the host; what the JS did with the
// world's answer (a message, a sound) happens when the command's result comes back (HudGame.Poll).
//
// No Godot types. Ported from remake/src/main.js (pw-exporter 98c7703), the part from "game API used by the HUD" to
// G.selectIdleWorker.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;

namespace PwRemake.UI.Hud.Logic;

/// <summary>A card of the command bar's flyout: an action of the tech tree, a stance, or the campaign's waiting mobile
/// base (main.js freeMainActions: a free build card of its own).</summary>
public sealed class HudAction
{
    public ActionDef? A;
    /// <summary>a stance card (AggroState_0..2): 0 aggressive .. 2 passive</summary>
    public int? Stance;
    /// <summary>the parked mobile base this card puts down (main.js a.freeMain)</summary>
    public Entity? FreeMain;
    public string? IconOverride;

    public string Id => A?.Id ?? (Stance != null ? "stance_" + Stance : "?");
    /// <summary>the location's uiposition subcat and menupos (Flyout.usl)</summary>
    public string? Subcat => FreeMain != null ? "ECON" : A?.Loc?.UiStr("subcat");
    public string? Cat => A?.Loc?.UiStr("cat");
    public string? MenuPos => FreeMain != null ? "0" : A?.Loc?.UiStr("menupos");
    public string? Icon => IconOverride ?? A?.Loc?.Icon;
    public override string ToString() => Id;
}

/// <summary>A command the HUD sent: what to do with the world's answer.</summary>
sealed record PendingCommand(Command C, Action<string?> OnResult);

public sealed class HudGame
{
    /// <summary>the reasons of the world, as the player reads them (main.js REASON)</summary>
    public static readonly Dictionary<string, string> REASON = new()
    {
        ["req"] = "Requirements not met", ["cost"] = "Not enough resources", ["housing"] = "Population limit reached – build more housing",
        ["level"] = "No free slot at this level of the army pyramid", ["full"] = "Production queue is full", ["done"] = "Already researched",
        ["place"] = "Cannot build here", ["skulls"] = "Not enough skulls", ["max"] = "Already at the highest level",
        ["down"] = "A unit keeps the level it has reached",
        ["cooldown"] = "Still recharging", ["unique"] = "You already have this hero", ["target"] = "Choose a target", ["storage"] = "Storage is full",
        ["busy"] = "Busy", ["illusion"] = "Illusions cannot do that", ["hidden"] = "Not available", ["disabled"] = "Not available",
        // MIRAGE's building limits (_MI_BuildingMaxReached; World.BuildingLimits)
        ["limit"] = "You've reached the maximum quantity of this building class.",
    };
    public static string Reason(string why) => REASON.TryGetValue(why, out var r) ? r : why;

    // special moves and building commands of the selection (generic commands have their own buttons)
    static readonly Regex GENERIC = new(@"^(Attack|Walk|Stop|Kill|AggressiveTarget|AggroState_\d|Formation_\d)$", RegexOptions.Compiled);
    static readonly Regex UNIT_TYPES = new(@"^(CHTR|ANML|VHCL|SHIP)$", RegexOptions.Compiled);

    readonly IHudContext H;
    /// <summary>a line in the middle of the screen (hud.message)</summary>
    public Action<string, string> Message = (_, _) => { };
    readonly List<PendingCommand> _pending = new();

    public HudGame(IHudContext host) { H = host; }

    World W => H.World;
    Player Me => H.Me;
    Rules D => H.World.Data;

    // ------------------------------------------------------------------ commands and their answers
    /// <summary>submits a command of the human player; onResult gets the world's reason (null: done) when it ran</summary>
    public void Send(Command c, Action<string?>? onResult = null)
    {
        c.Player = Me.Id;
        if (onResult != null) _pending.Add(new PendingCommand(c, onResult));
        H.Submit(c);
    }

    /// <summary>the answers of the commands that ran since the last call (once a frame)</summary>
    public void Poll()
    {
        if (_pending.Count == 0) return;
        var res = W.CommandResults;
        for (int i = res.Count - 1; i >= 0 && _pending.Count > 0; i--)
        {
            int k = _pending.FindIndex(p => ReferenceEquals(p.C, res[i].Command));
            if (k < 0) continue;
            var p = _pending[k];
            _pending.RemoveAt(k);
            p.OnResult(res[i].Reason);
        }
        // (a command the world dropped - a lost result list - is forgotten after a while)
        if (_pending.Count > 64) _pending.RemoveRange(0, _pending.Count - 64);
    }

    void Fail(string? why) { if (why != null) Message(Reason(why), "bad"); }

    // ------------------------------------------------------------------ what a selection offers
    /// <summary>the selection's own alive entities (hud.js: [...G.sel].filter(e => e.alive && e.owner === G.me))</summary>
    public List<Entity> OwnSelection() => H.Selection.Where(e => e.Alive && e.Owner == Me).ToList();

    /// <summary>actions offered by an entity (tech tree /Actions/&lt;tribe&gt;/.../locations); hidden ones left out</summary>
    public List<ActionDef> ProducerActions(Entity? e)
    {
        if (e == null || !e.Alive || e.Owner == null || (e is Building b && !b.Built)) return new List<ActionDef>();
        return ActionsOf(e).Where(a => a.Cat != "Build/BLDG" && a.Kind != "Moves" && a.Visible && !a.Disabled).ToList();
    }

    List<ActionDef> ActionsOf(Entity e) => D.ActionsOf(e.RulesOwner(), e.Name, e.Kind, e.Def?.Type ?? e.Cls);

    /// <summary>rally points: only for what produces units (barracks, harbours, mobile camps ...) and the moving
    /// harbours - towers, walls, farms and the like have none</summary>
    public bool CanRally(Entity? e) => e != null && e.Alive && e.Owner == Me && (
        (W.IsMovingHarbour(e) && e is Building { Built: true }) ||
        PwRemake.Sim.World.World.IsPortal(e) ||                // (a MIRAGE portal terminus is a rally site from its site on: its destination)
        ProducerActions(e).Any(a => a.Kind == "Build" && UNIT_TYPES.IsMatch(a.Type)));

    /// <summary>buildings the selected workers (or any worker of the player) can construct</summary>
    public List<HudAction> BuildActions()
    {
        Entity? w = H.Selection.FirstOrDefault(u => u.Alive && u.Owner == Me && u is Unit uu && uu.CanBuild)
            ?? W.Units.FirstOrDefault(u => u.Alive && u.Owner == Me && u.IsWorker && u.CanBuild);
        if (w == null) return new List<HudAction>();
        var owner = w.RulesOwner();
        // Taslow builds what the player's workers build (CommandBar.usl GetAvailableActions: "HACK!!! tesla can build
        // stuff too" - the class tesla_s0 is looked up as <tribe>_worker; the sequel's Taslov is a copy of him: Rules.kind)
        List<ActionDef> all = D.Kind(w.Name) == "tesla_s0"
            ? D.ActionsOf(owner, D.Tree(owner).Tribe.ToLowerInvariant() + "_worker", "unit", "CHTR")
            : ActionsOf(w);
        // harbours only on maps with water
        var acts = all.Where(a => a.Cat == "Build/BLDG" && a.Visible && !a.Disabled && (W.WaterNav != null || !(D.Def(a.Results[0].Obj, Me)?.Coastal ?? false)));
        return FreeMainActions().Concat(acts.Select(a => new HudAction { A = a })).ToList();
    }

    // the mobile base a campaign mission starts the Dustriders with (another tribe: the main building in its place) waits
    // until a worker puts it down (campaign/setup.js freeMains): a build card of its own, free and at once
    List<HudAction> FreeMainActions()
    {
        var list = Me.FreeMains.Where(u => u.Alive && u.Parked).ToList();
        if (list.Count == 0) return new List<HudAction>();
        var real = PwRemake.Sim.Rules.Rules.OwnActionOf(D.Actions(Me), list[0].Name);
        var a = new ActionDef
        {
            Id = "free_main", Kind = "Build", Cat = "Build/BLDG", Type = "BLDG", Visible = true, Tribe = real?.Tribe ?? Me.Tribe, Path = real?.Path ?? "",
            Loc = real?.Loc, Locs = real?.Locs ?? new List<ActionLoc>(), Results = new List<ActionResult> { new() { Obj = list[0].Name } },
        };
        return new List<HudAction> { new() { A = a, FreeMain = list[0], IconOverride = real?.Loc?.Icon ?? UiAtlasIcon(list[0].Name) ?? "aje_resource_collector_1" } };
    }
    static string? UiAtlasIcon(string name) => PwRemake.UI.Mission.Logic.UiAtlas.IconFor(name);

    public List<ActionDef> MoveActions(IEnumerable<Entity> sel)
    {
        var seen = new List<ActionDef>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in sel)
        {
            if (!e.Alive || e.Owner != Me || (e is Building b && !b.Built)) continue;
            foreach (var a in W.MovesOf(e)) if (!GENERIC.IsMatch(a.Id) && a.Visible && ids.Add(a.Id)) seen.Add(a);
        }
        return seen;
    }

    List<Entity> Casters(IEnumerable<Entity> sel, ActionDef a) => sel.Where(e => e.Alive && e.Owner == Me && W.MovesOf(e).Any(x => x.Id == a.Id)).ToList();

    /// <summary>why none of the selection can use the move now (null: one can): req | level | cooldown ...</summary>
    public string? MoveState(IEnumerable<Entity> sel, ActionDef a)
    {
        string why = "req";
        foreach (var e in Casters(sel, a))
        {
            var w = Moves.MOVES.ContainsKey(a.Id) ? W.MoveCheck(e, a) : D.Check(e.RulesOwner(), a, e);
            if (w == null) return null;
            if (why == "req" || w == "cooldown") why = w;
        }
        return why;
    }

    public double MoveCooldown(IEnumerable<Entity> sel, ActionDef a)
    {
        double m = 1e9;
        foreach (var e in Casters(sel, a)) m = Math.Min(m, W.CooldownLeft(e, a));
        return m;
    }

    public static int QueueCount(Entity? e, ActionDef a) => e == null ? 0 : e.Queue.Count(q => q.Action.Id == a.Id);

    /// <summary>why a card can't be used now (hud.js refreshFlyout why): moves by MoveState, the rest by CanQueue</summary>
    public string? Why(HudAction c, IReadOnlyList<Entity> sel)
    {
        if (c.Stance != null || c.A == null) return null;
        if (c.FreeMain != null) return null;
        var a = c.A;
        return a.Kind == "Moves" ? MoveState(sel, a) : W.CanQueue(Me, a, sel.Count > 0 ? sel[0] : null);
    }

    // ------------------------------------------------------------------ what a click does
    /// <summary>a card of the flyout pressed (G.useAction; shift: five times)</summary>
    public void UseAction(HudAction c, IReadOnlyList<Entity> sel, bool shift)
    {
        if (c.Stance != null) { SetStance(sel, c.Stance.Value); return; }
        var a = c.A!;
        H.UiSound("UI_click");
        if (a.Kind == "Moves")
        {
            var list = Casters(sel, a);
            Moves.MOVES.TryGetValue(a.Id, out var M);
            // a move with a target (not "self"): the next click chooses it (input.js target mode)
            if (M != null && !M.Self && list.Any(e => W.MoveCheck(e, a) == null))
            {
                H.SetInputMode("target", a);
                Message($"{D.Text(a.Id).Name}: choose a target", "");
                return;
            }
            var why = MoveState(list, a);
            if (why != null && M != null) { Fail(why); return; }
            Send(new UseMoveCommand { Casters = list.Select(e => e.Id).ToArray(), ActionId = a.Id, All = !(M != null && M.Self && a.Id != "Burn") }, Fail);
            return;
        }
        if (c.FreeMain != null) { H.SetInputMode("place", a); return; }
        if (a.Cat == "Build/BLDG")
        {
            if (D.Check(Me, a) != null) { Message(Reason("req"), "bad"); return; }
            if (W.LimitWhy(Me, a) is { } lim) { Message(Reason(lim), "bad"); return; }        // (MIRAGE's building limits)
            if (!Me.CanAfford(a.Cost)) { Message(Reason("cost"), "bad"); return; }
            H.SetInputMode("place", a);
            return;
        }
        var prod = sel.Count > 0 ? sel[0] : null;
        if (prod == null) return;
        // minelayer / corsair: mines and water turrets are built at a point in the water (choose it with the mouse)
        if (prod is Unit pu && pu.Naval && Regex.IsMatch(a.Results.Count > 0 ? a.Results[0].Obj : "", "mineship_mine|water_turret"))
        {
            var why = D.Check(prod.RulesOwner(), a, prod) ?? (Me.CanAfford(a.Cost) ? null : "cost");
            if (why != null) { Fail(why); return; }
            H.SetInputMode("lay", a);
            Message($"{(D.Text(a.Results[0].Obj).Name is { Length: > 0 } n ? n : a.Id)}: choose a spot in deep water", "");
            return;
        }
        // an upgrade of the object itself goes to every selected object of its class (QueueActionCommand.Also)
        var also = a.Kind == "Upgrades" && sel.Count > 1 ? sel.Skip(1).Where(e => e.Name == prod.Name).Select(e => e.Id).ToArray() : null;
        Send(new QueueActionCommand { Producer = prod.Id, Action = a.Path, Count = shift ? 5 : 1, Also = also }, Fail);
    }

    /// <summary>right click on a card: the last of that action in the producer's queue is cancelled (G.unqueue)</summary>
    public void Unqueue(Entity? e, ActionDef a)
    {
        if (e == null || !e.Queue.Any(q => q.Action.Id == a.Id)) return;
        Send(new UnqueueCommand { Producer = e.Id, ActionId = a.Id });
    }

    /// <summary>a click on a queue entry of the info window (world.cancelQueue(e, i)): cancels that entry</summary>
    public void CancelQueueAt(Entity e, int i)
    {
        if (i < 0 || i >= e.Queue.Count) return;
        // (the computer player's command of the same: cancel entry i - src/Sim/Ai/Hands.cs)
        Send(new PwRemake.Sim.Ai.AiCancelQueueCommand { Producer = e.Id, Index = i });
    }

    /// <summary>the stance of the selected units (the stance cards: u.stance = n)</summary>
    public void SetStance(IReadOnlyList<Entity> sel, int stance) => Send(new SetStanceCommand { Units = sel.OfType<Unit>().Select(u => u.Id).ToArray(), Stance = stance });

    public void ChangeLevel(Unit u, int lv) => Send(new ChangeLevelCommand { Unit = u.Id, Level = lv }, why =>
    {
        if (why != null && why != "same") { Message(Reason(why), "bad"); H.ErrorSound(); }
        else if (why == null) H.UiSound("ui_click_pyramid");
    });

    public void LevelUp(Unit u) => Send(new LevelUpCommand { Unit = u.Id }, Fail);

    /// <summary>Del: the first selected (Kill), Shift+Del: all of them (KillAll)</summary>
    public void KillSelected(int n = int.MaxValue)
    {
        var list = H.Selection.Where(x => x.Alive && x.Owner == Me && x.Kind != "res").Take(n).Select(x => x.Id).ToArray();
        if (list.Length > 0) Send(new KillCommand { Entities = list });
    }

    /// <summary>units: an order without a target (stop, unload: G.order(units, {type}))</summary>
    public void Order(IReadOnlyList<Entity> units, string type) =>
        Send(new OrderCommand { Units = units.OfType<Unit>().Select(u => u.Id).ToArray(), Type = type });

    // ------------------------------------------------------------------ selection (G.select, G.deselect)
    /// <summary>the own ones of the list, else its first (G.select)</summary>
    public void Select(IEnumerable<Entity> list)
    {
        var l = list.ToList();
        var own = l.Where(e => e.Owner == Me).ToList();
        H.SetSelection(own.Count > 0 ? own : l.Take(1).ToList());
    }
    public void Deselect(Entity e) => H.SetSelection(H.Selection.Where(x => x != e).ToList());

    /// <summary>does the human player see what player p sees? Own things always; in a campaign also those of players who
    /// are friends in both directions (G.sees)</summary>
    public bool Sees(Player? p) => p != null && (p == Me || (H.Campaign && Me.IsFriend(p) && p.IsFriend(Me)));
}
