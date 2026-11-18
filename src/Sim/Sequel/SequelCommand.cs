// What the player does in the remake's own campaigns beyond the normal orders, as a command (docs/ARCHITECTURE.md
// "Commands"; docs/SAVES.md: a saved game replays it): the briefing was read ("begin" - the mission's clock and script
// start), a command power used ("power": recon, supply, airdrop, artillery at x, z), and the debug tools' "objective
// done" / "win" / "lose" of these missions.
using System.Linq;
using PwRemake.Sim.Commands;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Sequel;

public sealed class SequelCommand : Command
{
    /// <summary>begin | power | objdone | win | lose</summary>
    public string What = "";
    /// <summary>power: its id; objdone: the objective's id</summary>
    public string Id = "";
    public double X, Z;

    public override string? Apply(W_ w)
    {
        if (w.Campaign is not SequelRuntime R) return "none";
        if (R.Me == null || R.Me.Id != Player) return "player";
        switch (What)
        {
            case "begin": R.Started = true; return null;
            case "power": return R.UsePower(Id, X, Z) ? null : R.PowerState(Id);
            case "objdone":
                {
                    var o = R.Objectives.FirstOrDefault(x => x.Id == Id) ?? R.Objectives.FirstOrDefault(x => !x.Done && !x.Failed);
                    if (o == null) return "none";
                    if (o.Of > 0) R.Progress(o.Id, o.Of); else R.Complete(o.Id);
                    return null;
                }
            case "win": R.Win(); return null;
            case "lose": R.Lose("Debug tools: mission lost."); return null;
        }
        return "unknown";
    }
}
