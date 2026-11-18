// The help book in a game (game/src/UI/Help): F1 or the pause menu's "Help". F1 opens it at the help item of what
// the player points at, as HelpMenu.StartHelpNav does: the hovered command button (the HUD's tooltip shown), the
// building being placed (its tech-tree description), the first selected object, the object under the mouse
// (GetObjHelpitemKey: the description key of its tech-tree entry, else its class) - the first the index has; else
// the introduction. The game stands still while it is open (the pause menu's MenuOpen).
//
// Ported from remake/src/ui/menu.js helpFor / helpBook and ui/input.js (F1) (pw-exporter 98c7703).
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.UI.Help;

namespace PwRemake.App;

public partial class GameSession
{
    /// <summary>opens the help book: at page ("file#key"), else at what F1 points at; Close / Esc go back to the game</summary>
    public void OpenHelp(string? page = null)
    {
        if (Cinematic || _menu != null) return;
        var at = page ?? HelpWindow.StartAt(HelpCandidates());
        OpenMenu();
        _menu?.Help(at, CloseMenu);
    }

    /// <summary>the help keys F1 tries, in StartHelpNav's order</summary>
    public List<string?> HelpCandidates()
    {
        var res = new List<string?>();
        if (HudView?.CurrentTip?.HelpKey is { } tip) { res.Add(tip); res.Add(DescOf(tip, Me)); }
        if (Input?.Mode == "place" && Input.ModeData is ActionDef a)
        {
            res.Add(a.Loc?.Desc ?? a.Locs.FirstOrDefault()?.Desc);
            if (a.Results.Count > 0) { res.Add(DescOf(a.Results[0].Obj, Me)); res.Add(a.Results[0].Obj); }
        }
        var sel = Selection?.Items.FirstOrDefault(e => e.Alive);
        if (sel != null) { res.Add(DescOf(sel.Name, sel.RulesOwner())); res.Add(sel.Name); }
        if (Input?.Hover is { Alive: true } h) { res.Add(DescOf(h.Name, h.RulesOwner())); res.Add(h.Name); }
        return res;
    }

    // HelpMenu.GetObjHelpitemKey: the description of the class's tech-tree entry ("_aje_tent", the heroes' "_lovelace")
    string? DescOf(string? name, IRulesOwner? owner)
    {
        if (string.IsNullOrEmpty(name) || World == null || !Rules.Exists(name)) return null;
        try { return Rules.Def(name, owner)?.Desc; } catch (System.Exception) { return null; }
    }
}
