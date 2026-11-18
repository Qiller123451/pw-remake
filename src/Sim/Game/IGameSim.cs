// What a running game is to the session that shows it, whatever was started: a campaign mission (MissionSession) or a
// skirmish (SkirmishSession) - the world on its map, the human player, where the camera starts, one simulation step,
// whether the game is over. GameSession (game/src/App) runs either through this.
using System.Collections.Generic;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Maps;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Game;

public interface IGameSim
{
    GameWorld World { get; }
    MapSource Map { get; }
    BuiltWorld Built { get; }
    /// <summary>the human player (null: none, e.g. a skirmish of computer players only)</summary>
    Player? Human { get; }
    /// <summary>where the camera starts (game x, z; yaw)</summary>
    (double X, double Z, double Yaw) StartCamera { get; }
    /// <summary>the game's name for the messages and menus (the mission title, the skirmish's map)</summary>
    string Title { get; }
    /// <summary>problems that did not stop the start (shown in the log)</summary>
    IReadOnlyList<string> Problems { get; }
    /// <summary>one simulation step (World.STEP = 50 ms)</summary>
    void Step();
    bool Over { get; }
    /// <summary>a hash of the whole game state (saved games compare it: docs/SAVES.md)</summary>
    ulong StateHash();
}
