// The player's selection (main.js G.sel, G.select, G.deselect, G.toggleSelect, G.selChanged): an ordered set of
// entities. Set() selects the player's own entities of a list - or, when there are none, its first one (an enemy,
// an animal, a resource: shown in the info window, not ordered). Dead and removed entities leave it by themselves
// (Prune, once per frame). Shift / Ctrl clicks and boxes follow the original's rule (Combine). Changed fires after every change (the HUD's info window and command bar, the input's
// control-group button state, the selection's speech).
// Ported from remake/src/main.js (G.sel ...) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.World;

namespace PwRemake.UI.PlayerInput;

public sealed class Selection
{
    readonly List<Entity> _items = new();
    readonly HashSet<Entity> _set = new();
    readonly Func<Player?> _me;
    /// <summary>the selection changed (silent: by itself - a death - not by the player: no speech)</summary>
    public event Action<Selection, bool>? Changed;

    public Selection(Func<Player?> me) { _me = me; }

    public IReadOnlyList<Entity> Items => _items;
    public int Count => _items.Count;
    public bool Contains(Entity? e) => e != null && _set.Contains(e);
    public Entity? First => _items.Count > 0 ? _items[0] : null;
    /// <summary>the own alive entities of the selection</summary>
    public List<Entity> Own => _items.Where(e => e.Alive && e.Owner != null && e.Owner == _me()).ToList();
    /// <summary>the own alive units (main.js / input.js myUnits)</summary>
    public List<Unit> OwnUnits => _items.OfType<Unit>().Where(u => u.Alive && u.Owner != null && u.Owner == _me()).ToList();

    /// <summary>G.select: the player's own entities of the list, else its first one</summary>
    public void Set(IEnumerable<Entity> list)
    {
        var l = list.Where(e => e != null).Distinct().ToList();
        var me = _me();
        var own = l.Where(e => e.Owner != null && e.Owner == me).ToList();
        _items.Clear(); _set.Clear();
        foreach (var e in own.Count > 0 ? own : l.Take(1)) { _items.Add(e); _set.Add(e); }
        Fire(false);
    }
    /// <summary>the selection exactly as given (the HUD applies G.select's rule itself); silent: no selection speech</summary>
    public void Replace(IEnumerable<Entity> list, bool silent = false)
    {
        _items.Clear(); _set.Clear();
        foreach (var e in list) if (e != null && _set.Add(e)) _items.Add(e);
        Fire(silent);
    }
    public void Add(Entity e) { if (_set.Add(e)) { _items.Add(e); Fire(false); } }
    public void AddRange(IEnumerable<Entity> list) { bool any = false; foreach (var e in list) if (_set.Add(e)) { _items.Add(e); any = true; } if (any) Fire(false); }
    public void Remove(Entity e) { if (_set.Remove(e)) { _items.Remove(e); Fire(true); } }
    public void Toggle(Entity e) { if (_set.Contains(e)) { _set.Remove(e); _items.Remove(e); } else { _set.Add(e); _items.Add(e); } Fire(false); }

    /// <summary>a click or a box with Shift and / or Ctrl held (GameInputController.usl MT_MouseLBtnUp): the selection
    /// becomes <see cref="Combine"/> of it and what was clicked or boxed</summary>
    public void Modify(IReadOnlyList<Entity> hovered, bool shift, bool ctrl) => Replace(Combine(_items, hovered, shift, ctrl, _me()));

    /// <summary>
    /// The original's Shift / Ctrl selection rule (GameInputController.usl MT_MouseLBtnUp, base and MIRAGE alike):
    /// one object clicked or boxed - Shift adds it (one already selected stays: Shift never removes), Ctrl adds it or
    /// takes it out again; several boxed - Shift: the boxed ones, then the selection; Ctrl: each boxed one in or out.
    /// Then the result is sorted out: with any of the player's own in it only his own stay; with any unit in it (a
    /// character, an animal, a vehicle, a ship - and the Aje floating harbour, MIRAGE's SEAS carrier) only the units stay,
    /// else with any building only the buildings, else all of it (resources, items ...). So Shift+click adds a building
    /// to selected buildings, and a building Shift+clicked to selected units is left out (units win over buildings).
    /// </summary>
    public static List<Entity> Combine(IReadOnlyList<Entity> current, IReadOnlyList<Entity> hovered, bool shift, bool ctrl, Player? me)
    {
        var tmp = new List<Entity>();
        void Include(Entity e) { if (e != null && !tmp.Contains(e)) tmp.Add(e); }
        if (hovered.Count == 1)
        {
            foreach (var e in current) Include(e);
            if (tmp.Contains(hovered[0])) { if (!shift) tmp.Remove(hovered[0]); }
            else Include(hovered[0]);
        }
        else if (shift)
        {
            foreach (var e in hovered) Include(e);
            foreach (var e in current) Include(e);
        }
        else if (ctrl)
        {
            foreach (var e in current) Include(e);
            foreach (var e in hovered) { if (!current.Contains(e)) Include(e); else tmp.Remove(e); }
        }
        else foreach (var e in hovered) Include(e);
        bool own = me != null && tmp.Any(e => e.Owner == me);
        bool units = tmp.Any(UnitLike), buildings = tmp.Any(e => e is Building);
        return tmp.Where(e => (!own || e.Owner == me) && (units ? UnitLike(e) : !buildings || e is Building)).ToList();
    }

    /// <summary>what the selection rule counts as a unit (CHTR / ANML / VHCL / SHIP, aje_floating_harbour, seas_carrier)</summary>
    public static bool UnitLike(Entity e) => e is Unit || e.Name is "aje_floating_harbour" or "seas_carrier";
    public void Clear() { if (_items.Count == 0) return; _items.Clear(); _set.Clear(); Fire(true); }

    /// <summary>the dead, removed and parked leave the selection (once per frame)</summary>
    public void Prune()
    {
        int n = _items.RemoveAll(e => !e.Alive || e.Removed || e.Parked);
        if (n == 0) return;
        _set.Clear(); foreach (var e in _items) _set.Add(e);
        Fire(true);
    }

    /// <summary>tells the listeners again (G.selChanged): after something changed what the selection can do</summary>
    public void Touch(bool silent = true) => Fire(silent);

    void Fire(bool silent) => Changed?.Invoke(this, silent);
}
