// A co-op campaign game (mode 2, docs/COOP.md) in the session: the co-op panel (CoopHud: seats, AI commander commands,
// "give to"), the commanders' points on the minimap. The rest is the simulation's (src/Sim/Coop) and the lockstep's.
using System.Linq;
using PwRemake.Sim.Coop;
using PwRemake.UI.Hud;

namespace PwRemake.App;

public partial class GameSession
{
    /// <summary>the co-op campaign game's simulation part (null: not one)</summary>
    public CoopRuntime? Coop => Mission?.Campaign.Coop;
    public CoopHud? CoopUi { get; private set; }

    /// <summary>_Ready's part: the co-op panel</summary>
    void ReadyCoop()
    {
        var co = Coop;
        if (co == null) return;
        CoopUi = new CoopHud(this, co) { Name = "CoopHud" };
        AddChild(CoopUi);
        CoopUi.Visible = !Cinematic;              // (hidden while a cutscene shows: the mission UI's OnCinematic, GameSession.cs)
        HudView.Overlays.Add(CoopUi.Panel);
        // the AI commanders' attack / defend points on the minimap (their seat's colour)
        var before = HudView.MinimapExtra;
        HudView.MinimapExtra = () => (before?.Invoke() ?? Enumerable.Empty<(double, double, int)>())
            .Concat(co.Seats.Where(s => s.Commander?.Point != null && s.Commander.Order is "attack" or "defend").Select(s => (s.Commander!.Point!.Value.X, s.Commander.Point.Value.Z, s.Player.Color)));
    }
}
