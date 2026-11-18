// A lockstep game's view of the host's seat for the mission's tutorial conditions (UNTT: "is the camera there", "is
// this selected"). Single player, the trigger engine asks the interface (PresenterTap records the answers); in a
// lockstep game every machine must get the same answer at the same step, so the host's seat sends its camera and
// selection as this command when they change (at most once a second) and every machine answers from it
// (LockstepPresenter, docs/MULTIPLAYER.md "Interface answers").
using System.Linq;

namespace PwRemake.Sim.Commands;

public sealed class UiStateCommand : Command
{
    /// <summary>the camera in MAP coordinates (as IMissionPresenter.Camera), or NaN when unknown</summary>
    public double X = double.NaN, Y = double.NaN, Dist, Yaw;
    /// <summary>the entity ids selected</summary>
    public int[] Selected = System.Array.Empty<int>();

    public override string? Apply(World.World w)
    {
        if (w.Campaign is not Campaign.Campaign C) return "none";
        C.SharedCamera = double.IsNaN(X) ? null : (X, Y, Dist, Yaw);
        C.SharedSelection = Selected.ToHashSet();
        return null;
    }
}
