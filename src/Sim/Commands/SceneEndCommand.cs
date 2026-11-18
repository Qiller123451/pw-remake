// The mission UI's answers to the trigger engine as commands: a cutscene (sequence) or a dialogue scene is over.
//
// The mission UI ends a scene when its time is up, its speech ended or the player skipped it - at a moment of the
// frames, not of the simulation. Called directly (TriggerEngine.EndSequence / EndDialog, as the JS did), the end
// reached the triggers between two steps chosen by the frame loop; as a command it is applied at the start of the next
// step and recorded with it, so a saved game (docs/SAVES.md), a replay and the other machines of a co-op game see it at
// the same step. Timeline: the cutscene player ran the scene's timeline and moved its actors (SeqActors.Moved: where
// they are left, snap back) - only the view knows that (a film that plays shows no timeline).
using PwRemake.Sim.World;

namespace PwRemake.Sim.Commands;

/// <summary>A cutscene ("sequence") or dialogue scene ("dialog", its Token) shown by the mission UI is over.</summary>
public sealed class SceneEndCommand : Command
{
    /// <summary>"sequence" | "dialog"</summary>
    public string Kind = "sequence";
    /// <summary>dialog: the engine's token of the scene (TriggerEngine.PlayDialog)</summary>
    public int Token;
    /// <summary>sequence: its timeline was played (the actors were moved by the scene)</summary>
    public bool Timeline;

    public override string? Apply(World.World w)
    {
        if (w.Campaign is not Campaign.Campaign C || C.Engine == null) return "none";
        if (Kind == "dialog") { C.Engine.EndDialog(Token); return null; }
        if (C.Engine.SeqRunning == null) return "none";
        if (C.Host?.SeqPlay?.Actors is { } a) a.Moved = Timeline;
        C.Engine.EndSequence();
        return null;
    }
}
