// The player's selection (main.js G.sel, G.select, G.deselect, G.toggleSelect, G.selChanged): an ordered set of
// entities. Set() selects the player's own entities of a list - or, when there are none, its first one (an enemy,
// an animal, a resource: shown in the info window, not ordered). Dead and removed entities leave it by themselves
// (Prune, once per frame). Changed fires after every change (the HUD's info window and command bar, the input's
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
