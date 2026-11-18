// Orders of the mouse (ui/input.js): what the selection can do (selectionInfo, CSelectionInfo), the cursor of a
// right click (cursorFor: GameInputController.usl GetAction, Game.usl CCursorMgr), the right click's order (orderOn:
// towers, moving harbours, rally points, items, wells, walls, transports, trade, attack, gather, build, repair,
// deliver, heal, move), the formation arrow, group moves in formation (moveGroup, game/formation.js), the armed
// modes' clicks (modeClick). Every order is a command of the human player (src/Sim/Commands) through the session.
// Ported from remake/src/ui/input.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;

namespace PwRemake.UI.PlayerInput;

public partial class GameInput
{
    static readonly Regex TradeCart = new("_cart$|^aje_trade_dino$");
    static readonly Regex Usable = new("^(hu_bunker|hu_small_wall|hu_re_enforced_wall|aje_clay_wall|ninigi_palisade|ninigi_bamboofarm|ninigi_paddy|hu_corn_field|seas_greenhouse|aje_slaughterhouse)$");

    sealed class SelInfo
    {
        public List<Entity> Own = new();
        public List<Unit> Units = new();
        public HashSet<string> Types = new();
        public bool Worker, Fish, Attack, Build;
        public List<Unit> Trade = new(), Healers = new();
        public bool HFood, HWood, HStone;
        public double MinRange = 1e9, MaxRange, Area;
        public Entity? MaxObj;
        public bool Harvest(string res) => res switch { "food" => HFood, "wood" => HWood, "stone" => HStone, _ => false };
    }

    // CSelectionInfo (GameInputController.usl): what the own part of the selection can do
    SelInfo SelectionInfo()
    {
        var I = new SelInfo { Own = G.Selection.Own };
        foreach (var e in I.Own)
        {
            I.Types.Add(e.Cls);
            if (e is Unit u)
            {
                I.Units.Add(u);
                if (u.Cls == "CHTR" && (u.Name.Contains("worker") || W.Data.Kind(u.Name) == "tesla_s0")) I.Worker = true;
                if (u.Naval && u.IsWorker) I.Fish = true;
                if (TradeCart.IsMatch(u.Name)) I.Trade.Add(u);
                if (u.Stats?.Heal != null) I.Healers.Add(u);
                if (u.IsWorker && !u.Naval && u.Stats?.Carry != null)
                {
                    if (u.Stats.Carry.Food > 0) I.HFood = true;
                    if (u.Stats.Carry.Wood > 0) I.HWood = true;
                    if (u.Stats.Carry.Stone > 0) I.HStone = true;
                }
                if (u.CanBuild) I.Build = true;
            }
            if (e.Name == "aje_floating_harbour") I.Fish = true;
            var cs = e.Weapons.Long != null ? e.Cs(e.Weapons.Long) : null;
            if (cs != null && cs.Dmg > 0)
            {
                I.Attack = true;
                I.Area = Math.Max(I.Area, cs.Splash);
                I.MinRange = Math.Min(I.MinRange, cs.MinRange);
                if (cs.Range > I.MaxRange) { I.MaxRange = cs.Range; I.MaxObj = e; }
            }
        }
        return I;
    }

    /// <summary>the cursor of a right click on h (or the ground point g); "" is the standard cursor (also over open
    /// ground: the original shows "walk" only while a move is armed)</summary>
    public string CursorFor(Entity? h, Vector3? g, bool shift)
    {
        var P = W.Play;
        if (g is Vector3 gp && (gp.X < P.X0 || gp.X > P.X1 || gp.Z < P.Z0 || gp.Z > P.Z1)) return "no";   // the map's edge
        var I = SelectionInfo();
        if (I.Own.Count == 0 || h == null) return "";
        // resources (trees, stone, fruit, carcasses, fish)
        if (h is ResNode n)
        {
            if (!G.Fog.Visible(n.Pos.X, n.Pos.Z) && n.Res == "wood") return "";
            if (n.Water) return I.Fish ? "harvest_food" : "";
            if (I.Fish && !I.HFood && !I.HWood && !I.HStone) return "";
            return I.Harvest(n.Res) ? "harvest_" + n.Res : "";
        }
        // an item: characters, animals and vehicles may take it (GetAction ITEM: the "trade" cursor)
        if (h is Item) return I.Units.Any(u => W.CanCarry(u)) ? "trade" : "";
        // a healing well: characters, animals, vehicles and ships may drink (GetAction FNTN)
        if (h is Well) return I.Units.Any(u => W.CanUseWell(u, h)) ? "healing" : "";
        // MIRAGE's SEAS well, finished, own or allied: characters, animals, vehicles (a site: the building's cursors)
        if (PwRemake.Sim.World.World.IsSeasWell(h) && I.Units.Any(u => W.CanUseWell(u, h))) return "healing";
        // MIRAGE's portal terminus: characters, animals, vehicles go in (GameInputController.usl:1474 "load")
        if (PwRemake.Sim.World.World.IsPortal(h) && h is Building { Built: true } && I.Units.Any(u => W.CanEnterPortal(u, h))) return "load";
        // (a nest belongs to nobody: IsEnemy holds for it as for the wild animals - GameInputController.usl:1111)
        bool wild = h is Unit hu && hu.Wild;
        bool enemy = wild || h.IsNest || (h.Owner != null && W.HostileTo(G.Me, h));
        if (enemy)
        {
            if (wild && h.Cls == "ANML" && I.Worker) return "attack_harvest";
            if (!I.Attack) return "";
            if (I.MaxObj != null)
            {
                double d = Math.Sqrt((h.Pos.X - I.MaxObj.Pos.X) * (h.Pos.X - I.MaxObj.Pos.X) + (h.Pos.Z - I.MaxObj.Pos.Z) * (h.Pos.Z - I.MaxObj.Pos.Z));
                if (d > I.MaxObj.Radius + h.Radius + I.MaxRange) return "attack_walk";
                if (I.Area > 0 && d < I.MinRange + I.MaxObj.Radius) return "attack_area_weapon_invalid";
            }
            return "attack";
        }
        if (h.Owner != null && !G.Me.IsFriend(h.Owner) && h.Owner != G.Me) return "";
        // own and allied objects
        bool usable = h is Building && (Usable.IsMatch(h.Name) || W.IsFarm(h));
        if (I.Healers.Count > 0 && h is Unit && (shift || !(W.Capacity(h) > 0)) && h.Hp < h.MaxHp && (I.Healers.Count > 1 || !I.Healers.Contains(h))) return "healing";
        if (W.Capacity(h) > 0 && I.Units.Any(u => u != h && W.CanBoard(u, h)))
        {
            if (h is Building hb0 && I.Build && (!hb0.Built || ((shift || !usable) && h.Hp < h.MaxHp))) return "build";
            int pax = h is Unit tu ? tu.Passengers?.Count ?? 0 : 0;
            if (pax >= W.Capacity(h)) return "";
            return "load";
        }
        if (h is Building b)
        {
            if (I.Build && b.Owner == G.Me)
            {
                if (!b.Built) return "build";
                if ((shift || !usable) && b.Hp < b.MaxHp) return "build";
                // (MIRAGE's mines hu_mine / seas_mine: "harvest_stone" - GameInputController.usl:1466)
                if (W.IsFarm(b) && b.Workers.Count < W.FarmSlots(b)) return b.Name == "ninigi_bamboofarm" ? (I.HWood ? "harvest_wood" : "") : PwRemake.Sim.World.World.IsMineField(b) ? (I.HStone ? "harvest_stone" : "") : (I.HFood ? "harvest_food" : "");
            }
            if (I.Trade.Count > 0 && W.IsTradeBuilding(b) && I.Trade.Any(u => u.TradeHome != b)) return "trade";
        }
        return "";
    }

    // ======================================================================================== orders
    void Marker(double x, double z, int color) => G.Overlay.Marker(x, z, color);
    /// <summary>the object a rally point is set on (FightingObj.SetRallyPoint): a resource, MIRAGE's portal (its destination,
    /// the units go through) or Tesla machine (the units come out there) - else none</summary>
    int RallyTargetId(Entity? t) => t is ResNode || PwRemake.Sim.World.World.IsPortal(t) || PwRemake.Sim.World.World.IsTeslaMachine(t) ? t!.Id : 0;
    static int[] Ids(IEnumerable<Entity> l) => l.Select(e => e.Id).ToArray();

    /// <summary>an order to units (G.order): move, attackmove, attack, stop, gather, build, repair, heal, board ...</summary>
    public void Order(IEnumerable<Unit> units, string type, Entity? target = null, double x = 0, double z = 0, bool queue = false)
        => G.Submit(new OrderCommand { Units = Ids(units), Type = type, Target = target?.Id ?? 0, X = x, Z = z, Queue = queue });

    void RightClick(Vector2 m, bool shift, bool ctrl)
    {
        if (Mode != null) { SetMode(null); return; }
        OrderOn(EntityAt(m), GroundPoint(m), shift, ctrl);
    }

    /// <summary>the right-click's order for the selection: on the object t (or nothing) at the ground point g</summary>
    public void OrderOn(Entity? t, Vector3? g, bool shift, bool ctrl = false)
    {
        var units = MyUnits();
        var sel = G.Selection.Items;
        // towers: right-click an enemy = shoot at it
        var towers = sel.Where(e => e.Alive && e.Owner == G.Me && W.CanAimTower(e)).ToList();
        if (towers.Count > 0 && t != null && W.HostileTo(G.Me, t))
        {
            G.Submit(new AimTowersCommand { Towers = Ids(towers), Target = t.Id });
            G.PlayUi("UI_click");
            Marker(t.Pos.X, t.Pos.Z, 0xff3a2a);
            if (units.Count == 0) return;
        }
        // moving harbours (SEAS carrier, Aje floating harbour): right-click sails there, Shift+right-click = rally point
        var harbours = sel.Where(e => e.Alive && e.Owner == G.Me && W.IsMovingHarbour(e) && e is Building { Built: true }).ToList();
        if (harbours.Count > 0 && units.Count == 0 && !shift && g is Vector3 hg)
        {
            G.Submit(new MoveHarbourCommand { Harbours = Ids(harbours), X = hg.X, Z = hg.Z }, why =>
            {
                if (why == null) { Marker(hg.X, hg.Z, 0x7dff6a); G.PlayUi("UI_click"); }
                else { G.Message("It can only sail on water", "bad"); G.PlayError(); }
            });
            return;
        }
        if (units.Count == 0)
        {
            // rally point for selected production buildings / collector
            var prod = sel.Where(e => G.CanRally(e)).ToList();
            if (prod.Count > 0 && g is Vector3 rg)
            {
                // (MIRAGE: on a portal terminus - a terminus takes it as its destination, other producers send their units
                // into it; on a Tesla machine - the units come out there: RallyTargetId)
                G.Submit(new SetRallyCommand { Producers = Ids(prod), X = rg.X, Z = rg.Z, Target = RallyTargetId(t) });
                Marker(rg.X, rg.Z, 0xffe070);
                G.PlayUi("ui_rallypoint_land_set");
            }
            return;
        }
        // an item: the first selected unit that can carry it and has room goes for it - the last one even when its hands
        // are full, to hear that they are (GameInputController.usl HandleItem: "PickUp")
        if (t is Item)
        {
            var can = units.Where(u => W.CanCarry(u)).ToList();
            var u = can.FirstOrDefault(x => x.Item == null) ?? can.LastOrDefault();
            if (u != null) { Order(new[] { u }, "pickup", t); G.PlayOrdered("pickup", new[] { u }); Marker(t.Pos.X, t.Pos.Z, 0xffe070); return; }
        }
        // a healing well: who may drink there goes (GameInputController.usl: "/FountainHeal" for the selection)
        if (t is Well || PwRemake.Sim.World.World.IsSeasWell(t))
        {
            var ok = units.Where(u => W.CanUseWell(u, t)).ToList();
            if (ok.Count > 0) { Order(ok, "fountain", t); G.PlayOrdered("heal", ok); Marker(t.Pos.X, t.Pos.Z, 0x7dff6a); return; }
        }
        // a MIRAGE portal terminus (GameInputController.usl:1026 "EnterPortal" for characters, animals and vehicles): into it
        // when it works, else they walk there (the simulation decides: World.PortalOrder)
        if (PwRemake.Sim.World.World.IsPortal(t) && t is Building ptb && ptb.Built && G.Me.IsFriend(ptb.Owner))
        {
            var ok = units.Where(u => W.CanEnterPortal(u, ptb)).ToList();
            if (ok.Count > 0)
            {
                Order(ok, "portal", ptb); G.PlayOrdered("move", ok); Marker(ptb.Pos.X, ptb.Pos.Z, 0x7dff6a);
                if (ok.Count == units.Count) return;
                units = units.Where(u => !ok.Contains(u)).ToList();
            }
        }
        // walls (GameInputController.usl HandleOwnBuilding): siege towers and brachiosaurs dock to a wall piece; characters
        // go up an own walkable wall ("Use") or climb a ladder of their side - Shift on a damaged one repairs instead
        if (t is Building tb && tb.Alive)
        {
            var docks = units.Where(u => W.CanDock(u)).ToList();
            if (docks.Count > 0 && tb.Def?.WallKind == "wall" && tb.Tile != null)
            {
                Order(docks, "dock", tb);
                G.PlayOrdered("move", docks);
                Marker(tb.Pos.X, tb.Pos.Z, 0x7dff6a);
                if (docks.Count == units.Count) return;
            }
            var chars = units.Where(u => u.Cls == "CHTR" && (PwRemake.Sim.World.World.CanClimb(u) || u.OnWall != null)).ToList();
            bool repair = shift && tb.Hp < tb.MaxHp && units.Any(u => u.CanBuild);
            if (chars.Count > 0 && tb.Built && !repair && ((tb.Owner == G.Me && W.WalkableWall(tb)) || (PwRemake.Sim.World.World.IsLadder(tb) && G.Me.IsFriend(tb.Owner) && tb.DockTile != null)))
            {
                Order(chars, PwRemake.Sim.World.World.IsLadder(tb) ? "climb" : "use", tb);
                G.PlayOrdered("move", chars);
                Marker(tb.Pos.X, tb.Pos.Z, 0x7dff6a);
                return;
            }
        }
        // board a transport / enter a bunker
        if (t != null && t.Alive && t.Owner == G.Me && t != units[0] && W.Capacity(t) > 0 && units.Any(u => W.CanBoard(u, t)))
        {
            Order(units.Where(u => W.CanBoard(u, t)), "board", t);
            G.PlayOrdered("move", units);
            Marker(t.Pos.X, t.Pos.Z, 0x7dff6a);
            return;
        }
        // trade carts: right-click another market
        var carts = units.Where(u => TradeCart.IsMatch(u.Name)).ToList();
        if (t != null && carts.Count > 0 && W.IsTradeBuilding(t) && G.Me.IsFriend(t.Owner)) { Order(carts, "trade", t); G.PlayOrdered("trade", carts); Marker(t.Pos.X, t.Pos.Z, 0xffe070); return; }
        if (t != null && t.Alive && units[0].IsEnemy(t) && (t is Unit || t is Building))
        {
            Order(units, "attack", t);
            G.PlayOrdered("attack", units);
            Marker(t.Pos.X, t.Pos.Z, 0xff3a2a);
            return;
        }
        // fish shoals are for fishing boats, everything else for land workers
        var workers = units.Where(u => u.IsWorker && (t is ResNode rn ? rn.Water == u.Naval : !u.Naval)).ToList();
        var others = units.Where(u => !workers.Contains(u)).ToList();
        if (t != null && workers.Count > 0)
        {
            if (t is ResNode r)
            {
                Order(workers, "gather", t);
                G.PlayOrdered(r.Res, workers);
                if (others.Count > 0) MoveGroup(others, t.Pos.X, t.Pos.Z, "move", false, false);
                Marker(t.Pos.X, t.Pos.Z, 0xffe070);
                return;
            }
            if (t is Building b && b.Owner == G.Me)
            {
                if (!b.Built)
                {
                    Order(workers, "build", t);
                    G.PlayOrdered("build", workers);
                    if (others.Count > 0) MoveGroup(others, t.Pos.X, t.Pos.Z, "move", false, false);
                    Marker(t.Pos.X, t.Pos.Z, 0x7dff6a);
                    return;
                }
                if (W.IsFarm(b))
                {
                    Order(workers, "gather", t);
                    G.PlayOrdered(b.Def!.Unlimited.FirstOrDefault() ?? "food", workers);
                    Marker(t.Pos.X, t.Pos.Z, 0xffe070);
                    return;
                }
                if (b.Hp < b.MaxHp && !(b.IsDropoff && workers.Any(w => w.Carry is { Amount: > 0 })))
                {
                    Order(workers, "repair", t);
                    G.PlayOrdered("build", workers);
                    Marker(t.Pos.X, t.Pos.Z, 0x7dff6a);
                    return;
                }
                if (b.IsDropoff)
                {
                    var carrying = workers.Where(w => w.Carry is { Amount: > 0 }).ToList();
                    if (carrying.Count > 0)
                    {
                        G.Submit(new DeliverCommand { Workers = Ids(carrying), Target = b.Id });
                        Marker(t.Pos.X, t.Pos.Z, 0x7dff6a);
                        return;
                    }
                }
            }
        }
        // the others who can build (heroes like Tesla, the Dustriders' fighters: can_build) build up or repair an own building
        var builders = units.Where(u => u.CanBuild).ToList();
        if (t is Building ob && builders.Count > 0 && ob.Owner == G.Me && (!ob.Built || ob.Hp < ob.MaxHp))
        {
            Order(builders, ob.Built ? "repair" : "build", ob);
            G.PlayOrdered("build", builders);
            var rest = units.Where(u => !builders.Contains(u)).ToList();
            if (rest.Count > 0) MoveGroup(rest, ob.Pos.X, ob.Pos.Z, "move", false, false);
            Marker(ob.Pos.X, ob.Pos.Z, 0x7dff6a);
            return;
        }
        // medics: right-click on a wounded own unit heals it
        var healers = units.Where(u => u.Stats?.Heal != null).ToList();
        if (t is Unit tu2 && tu2.Owner == G.Me && healers.Count > 0 && tu2.Hp < tu2.MaxHp && !healers.Contains(tu2))
        {
            Order(healers, "heal", tu2);
            G.PlayOrdered("heal", healers);
            var rest = units.Where(u => u.Stats?.Heal == null).ToList();
            if (rest.Count > 0) MoveGroup(rest, tu2.Pos.X, tu2.Pos.Z, "move", false, false);
            return;
        }
        if (g is not Vector3 gg) return;
        MoveGroup(units, gg.X, gg.Z, "move", shift, ctrl);
        G.PlayOrdered("move", units);
        Marker(gg.X, gg.Z, 0x7dff6a);
    }

    // the formation arrow's spacing: close for a short arrow, open for a long one - as wide as the arrow is long, at least
    // the command bar's 2.7807
    static double ArrowSpacing(double len, int n) { int cols = (int)Math.Ceiling(Math.Sqrt(n * 1.6)); return Math.Max(Formation.SPACING, Math.Min(8, len / Math.Max(1, cols - 1))); }

    // the formation arrow while the right button is held: from the press point to the cursor, with the places
    void DragArrow(Vector2 m)
    {
        if (GroundPoint(m) is not Vector3 g || _rg is not Vector3 rg) return;
        double dx = g.X - rg.X, dz = g.Z - rg.Z, len = Math.Sqrt(dx * dx + dz * dz);
        if (Arrow == null && len <= 2.0) return;                       // (AbsSquare() > 4.0)
        var units = MyUnits().Where(u => !u.Inside).ToList();
        if (units.Count == 0) { Arrow = null; return; }
        var a = new FormationArrow { X = rg.X, Z = rg.Z, Dir = Math.Atan2(dz, dx), Len = len, Spacing = ArrowSpacing(len, units.Count), TipX = g.X, TipZ = g.Z };
        a.Slots = Formation.FormationSlots(W, units, a.X, a.Z, a.Dir, a.Spacing).Select(s => (s.X, s.Z)).ToList();
        Arrow = a;
    }

    void ArrowOrder(FormationArrow a, bool shift, bool ctrl)
    {
        var units = MyUnits().Where(u => !u.Inside).ToList();
        if (units.Count == 0) return;
        G.Submit(new GroupMoveCommand { Units = Ids(units), Type = "move", X = a.X, Z = a.Z, Dir = a.Dir, Spacing = a.Spacing, Queue = shift, Patrol = ctrl });
        G.PlayOrdered("move", units);
        Marker(a.X, a.Z, 0x7dff6a);
    }

    /// <summary>a group order to a point in formation (moveGroup): queue (Shift) after the orders it has; patrol (Ctrl)
    /// to and fro from where it is</summary>
    public void MoveGroup(IReadOnlyList<Unit> units, double x, double z, string type, bool queue, bool patrol)
    {
        if (units.Count == 0) return;
        G.Submit(new GroupMoveCommand { Units = Ids(units), Type = type, X = x, Z = z, Queue = queue, Patrol = patrol });
    }

    // ======================================================================================== the armed modes' clicks
    void ModeClick(Vector2 m, bool shift)
    {
        var units = MyUnits();
        var g = GroundPoint(m);
        var t = EntityAt(m);
        var mode = Mode;
        var sel = G.Selection.Items;
        // the minimap's ping button: a click on the ground pings that point (SecondaryInputController.usl:329-337)
        if (mode == "mapping") { if (g is Vector3 pg) G.MapPing(pg.X, pg.Z); SetMode(null); return; }
        if (mode == "place" && _ghost is { Wall: true } gw)
        {
            // walls: press = the line starts here (after a Shift corner the line already follows the mouse)
            if (g is not Vector3 wg) return;
            if (!gw.Active) { gw.Active = true; gw.Start = (wg.X, wg.Z); gw.Held = new(); }
            gw.Pressed = true;
            return;
        }
        if (mode == "callback")
        {
            var cb = ModeData as Action<double, double, Entity?>;
            SetMode(null);
            if (g is Vector3 cg) cb?.Invoke(cg.X, cg.Z, t);
            return;
        }
        if (mode == "repair")
        {
            if (t is Building rb && rb.Owner == G.Me) { Order(units.Where(u => u.CanBuild), rb.Built ? "repair" : "build", rb); Marker(rb.Pos.X, rb.Pos.Z, 0x7dff6a); }
            if (!shift) SetMode(null);
            return;
        }
        if (mode == "target" && ModeData is ActionDef ta)
        {
            // special move that needs a target: the selected units that have the move use it on the clicked unit / point
            var casters = sel.Where(e => e.Alive && e.Owner == G.Me && W.MovesOf(e).Any(x => x.Id == ta.Id)).ToList();
            Moves.MOVES.TryGetValue(ta.Id, out var M);
            G.Submit(new UseMoveCommand
            {
                Casters = Ids(casters), ActionId = ta.Id, Target = t is ResNode || t == null ? 0 : t.Id, HasGround = g != null, X = g?.X ?? 0, Z = g?.Z ?? 0,
                All = M != null && M.Self,
            }, why => { if (why != null && why != "cooldown") G.Message(G.Reason(why), "bad"); });
            G.PlayOrdered("attack", casters);
            if (!shift) SetMode(null);
            return;
        }
        if (mode == "lay" && ModeData is ActionDef la)
        {
            // minelayer / corsair: the nearest selected ship that can build it goes there and lays it
            var ships = sel.OfType<Unit>().Where(e => e.Alive && e.Owner == G.Me && e.Naval && W.Data.ActionsOf(e.RulesOwner(), e.Name, e.Kind, e.Def?.Type ?? e.Cls).Any(x => x.Id == la.Id)).ToList();
            if (g is Vector3 lg && ships.Count > 0)
            {
                var ship = ships.OrderBy(p => Math.Sqrt((p.Pos.X - lg.X) * (p.Pos.X - lg.X) + (p.Pos.Z - lg.Z) * (p.Pos.Z - lg.Z))).First();
                G.Submit(new LayWaterThingCommand { Ship = ship.Id, Action = la.Path, X = lg.X, Z = lg.Z, Queue = shift }, why =>
                {
                    if (why != null) { G.Message(why == "place" ? "Must be in deep water, away from other mines" : G.Reason(why), "bad"); G.PlayError(); }
                    else { Marker(lg.X, lg.Z, 0x7dff6a); G.PlayOrdered("move", new[] { ship }); }
                });
            }
            if (!shift) SetMode(null);
            return;
        }
        if (mode == "place") { PlaceClick(shift); return; }
        if (mode == "rally")
        {
            if (g is Vector3 rg)
            {
                G.Submit(new SetRallyCommand { Producers = Ids(sel.Where(e => G.CanRally(e))), X = rg.X, Z = rg.Z, Target = RallyTargetId(t) });
                Marker(rg.X, rg.Z, 0xffe070);
            }
        }
        else if (mode == "move" && units.Count == 0)
        {
            var hb = sel.Where(b => b.Alive && b.Owner == G.Me && W.IsMovingHarbour(b)).ToList();
            if (g is Vector3 mg) { if (hb.Count > 0) G.Submit(new MoveHarbourCommand { Harbours = Ids(hb), X = mg.X, Z = mg.Z }); Marker(mg.X, mg.Z, 0x7dff6a); }
        }
        else if (mode == "attack" && units.Count == 0)
        {
            // towers only
            var towers = sel.Where(e => e.Alive && e.Owner == G.Me && W.CanAimTower(e)).ToList();
            if (t != null && W.HostileTo(G.Me, t) && towers.Count > 0) { G.Submit(new AimTowersCommand { Towers = Ids(towers), Target = t.Id }); Marker(t.Pos.X, t.Pos.Z, 0xff3a2a); }
            else { G.Message("Choose an enemy for the tower to shoot at", "bad"); G.PlayError(); }
        }
        else if (units.Count > 0)
        {
            // the Attack command also takes a neutral player's object (that means war, world.order); never a friend's
            if (mode == "attack" && t != null && t is not ResNode && t.Owner != null && t.Owner != G.Me && !units[0].IsEnemy(t) && !W.AttackAllowed(units[0], t))
            { G.Message($"The {t.Owner.Name} are your allies - you cannot attack them", "bad"); G.PlayError(); }
            else if (mode == "attack" && t != null && (units[0].IsEnemy(t) || (t is not ResNode && W.AttackAllowed(units[0], t))))
            {
                var towers = sel.Where(e => e.Alive && e.Owner == G.Me && W.CanAimTower(e)).ToList();
                if (towers.Count > 0) G.Submit(new AimTowersCommand { Towers = Ids(towers), Target = t.Id });
                Order(units, "attack", t);
                Marker(t.Pos.X, t.Pos.Z, 0xff3a2a);
            }
            else if (g is Vector3 og) { MoveGroup(units, og.X, og.Z, mode == "move" ? "move" : "attackmove", false, false); Marker(og.X, og.Z, mode == "move" ? 0x7dff6a : 0xff3a2a); }
            G.PlayOrdered(mode == "move" ? "move" : mode == "attack" ? "attack" : "amove", units);
        }
        if (!shift) SetMode(null);
    }
}
