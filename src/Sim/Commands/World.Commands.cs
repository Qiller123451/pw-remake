// The world's side of the commands (docs/ARCHITECTURE.md "Commands"): a queue ordered by step and submission; the
// commands due are applied at the start of World.Step, before anything else of the step.
using System.Collections.Generic;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public sealed partial class World
{
    static readonly Logger LC = Log.For("World.Commands");
    readonly List<Command> _commands = new();
    long _cmdSeq;
    /// <summary>what the commands of the last steps did (the interface shows the reasons; drained like Events)</summary>
    public readonly List<CommandResult> CommandResults = new();
    /// <summary>every command applied, in order (a replay; null: not recorded)</summary>
    public List<Command>? Recorded;

    /// <summary>queues a command (Step 0: the next step)</summary>
    public void Enqueue(Command c)
    {
        if (c.Step <= StepNo) c.Step = StepNo + 1;
        c.Seq = ++_cmdSeq;
        // in order of (step, submission)
        int i = _commands.Count;
        while (i > 0 && (_commands[i - 1].Step > c.Step || (_commands[i - 1].Step == c.Step && _commands[i - 1].Seq > c.Seq))) i--;
        _commands.Insert(i, c);
    }

    public int PendingCommands => _commands.Count;
    /// <summary>the commands waiting for their step, in the order they will be applied (a saved game keeps them)</summary>
    public IReadOnlyList<Command> PendingCommandList => _commands;
    /// <summary>drops the commands waiting (a replay that went another way than the saved game)</summary>
    public void ClearPendingCommands() => _commands.Clear();

    /// <summary>the commands due at this step (World.Step)</summary>
    void ApplyCommands()
    {
        while (_commands.Count > 0 && _commands[0].Step <= StepNo)
        {
            var c = _commands[0];
            _commands.RemoveAt(0);
            string? why;
            try { why = c.Apply(this); }
            catch (System.Exception e) { LC.Error($"command {c} failed", e); why = "error"; }
            LC.Debug(() => $"{c}: {why ?? "ok"}");
            CommandResults.Add(new CommandResult(c, why));
            if (CommandResults.Count > 1000) CommandResults.RemoveRange(0, 500);
            c.Step = StepNo;                 // (the step it was applied at: a replay applies it there again)
            Recorded?.Add(c);
        }
    }
}
