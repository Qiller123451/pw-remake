// A request to a computer player: the chat line "AI<n>: <word>" to all (ChatWindow.usl:147-153 ->
// CEvt_AiGenericStringEvent) or the diplomacy window's request - help / food / wood / stone / gift
// (Ai/Chat.cs OnPlayerRequest; its answer comes as the computer player's "msg" event).
// Ported from remake/src/ui/chat.js send (pw-exporter 98c7703).
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Commands;

public sealed class AiRequestCommand : Command
{
    /// <summary>the computer player's slot (Player.Id)</summary>
    public int Slot;
    /// <summary>the request: "help" | "food" | "wood" | "stone" | "gift" (the first word counts)</summary>
    public string Word = "";

    public override string? Apply(W_ w)
    {
        var me = Cmd.PlayerOf(w, Player);
        if (me == null || w.Ai == null) return "none";
        var reply = w.Ai.PlayerRequest(Slot, me, Word);
        return reply == null ? "noanswer" : null;
    }
}
