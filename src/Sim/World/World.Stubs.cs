// STUBS: the calls of world.js / entities.js / main.js into the systems (game/systems/*.js) that need a value back or
// are called from outside World (entities, commands, delayed calls) were stand-ins here with a neutral answer while
// the systems were ported in parallel. Every system is ported now and every stub has been replaced by its real method
// (docs/SIMULATION.md "Stubs" lists where each one went). This file stays as the place for a new stand-in when a future
// port calls into a system that does not exist yet: add it at the END with a comment `// stub for <port>: ...` and call
// Stub(nameof(...)) in it - it logs once (World.Stubs, Debug) the first time it is called, so a run shows the gaps.
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public sealed partial class World
{
    static readonly Logger LS = Log.For("World.Stubs");
    static void Stub(string name) { if (LS.IsOn(LogLevel.Debug) && Log.FirstTime("stub|" + name)) LS.Debug($"stub {name} called (its system is not ported yet)"); }
}
