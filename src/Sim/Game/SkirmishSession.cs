// A skirmish as the game runs it, without the engine: a multiplayer map (.ula of the installation, or a random jungle)
// and the setup (Skirmish.cs: up to sixteen slots - the human and computer players, their tribes, teams, colours,
// handicaps and start positions -, the game type, the army budget) -> the world with its landscape, the players'
// start (the tribe's base and three workers and the start resources, or the army bought with the points:
// PointBuy), the map's own objects, wells, lakes, items, the end of the game (GameOverMgr: deathmatch / domination /
// defender, the warp gate) and one computer player per AI slot (AiPlayers.ForSkirmish) with its own army for the
// budget. Step() runs one simulation step (50 ms).
//
// Ported from remake/src/main.js (startGame for a skirmish: buildWorld's players part skirmishPlayers, gameOverInit,
// createBrains with cfg.slots) and remake/src/game/skirmish.js resolveSetup (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Game;

public sealed class SkirmishSession : IGameSim
{
    static readonly Logger L = Log.For("Skirmish");
    public static string[] TRIBES => Skirmish.TRIBES;
    /// <summary>the tribes' names (the browser remake's TRIBE_INFO)</summary>
    public static string TribeName(string t) => t switch { "Hu" => "Norsemen", "Aje" => "Dustriders", "Ninigi" => "Dragon Clan", "SEAS" => "SEAS", _ => t };

    public readonly SkirmishSetup Setup;
    public MapSource Map { get; private set; } = null!;
    public BuiltWorld Built { get; private set; } = null!;
    public GameWorld World => Built.World;
    public AiPlayers Ai = null!;
    public PointBuy PB = null!;
    /// <summary>the human player (null: every slot is a computer player)</summary>
    public Player? Human { get; private set; }
    /// <summary>the players' main buildings (or the Aje resource collector), by player</summary>
    public readonly List<Entity> Homes = new();
    /// <summary>the slots as they are played (tribe, start position, team and behaviour resolved), by player id</summary>
    public IReadOnlyList<SkirmishSlot> Resolved => _resolved;
    public (double X, double Z, double Yaw) StartCamera { get; private set; }
    public string Title => string.IsNullOrEmpty(Setup.MapName) ? Map.Name : Setup.MapName;
    public readonly List<string> Problems = new();
    IReadOnlyList<string> IGameSim.Problems => Problems;
    /// <summary>the setup's "Disable fog of war" (FOWEnabled off): the whole map is seen</summary>
    public bool RevealAll => !Setup.Fog;
    /// <summary>a saved game's setup (SaveGame.Setup) or GameStart.Skirmish</summary>
    public static SkirmishSetup? SetupOf(GameStart start, JsonNode? saved) =>
        start.Skirmish ?? (saved != null ? SkirmishSetup.FromJson(saved.ToJsonString()) : null);

    SkirmishSession(SkirmishSetup s) { Setup = s; }

    /// <summary>sets the skirmish up on the map (.ula of the installation; null or "": a random jungle of the setup's
    /// seed). aiData: the configuration's ai.json (null: the computer player's built-in tables). progress: (0..1, what)</summary>
    /// <param name="aiControlScript">the installation's Scripts/Ai/Modules/AiModuleControlDefault.usl (MIRAGE's gifts:
    /// AiData.ReadControlScript; null: ai.json's)</param>
    /// <param name="flags">how the simulation runs besides the setup (the game: SimFlags.Game - the world's fog of war,
    /// events, clip sounds; a saved game: its own; null: none, headless)</param>
    public static SkirmishSession Create(SkirmishSetup setup, string? ulaPath, Rules.Rules rules, ModelManifest models, ForestData? forest = null, GroundBrushes? brushes = null, JsonNode? aiData = null, Action<double, string>? progress = null, string? aiControlScript = null, SimFlags? flags = null, MapSource? map = null)
    {
        void Step(double k, string what) { try { progress?.Invoke(k, what); } catch (Exception) { } }
        var s = new SkirmishSession(setup);
        Step(0.1, "Reading the map");
        int players = Skirmish.ActiveSlots(setup).Count;
        // (map: a map source made by the caller - the map editor's Test map plays its map from memory, EditorPlaytest)
        s.Map = map ?? (string.IsNullOrEmpty(ulaPath)
            ? MapSource.GeneratedSource(new MapSourceConfig { Players = players, Seed = setup.Seed })
            : WorldBuilder.MapSourceOf(ulaPath, rules, models, null, forest));
        var S = s.Map;
        var opts = new WorldOptions
        {
            Seed = setup.Seed, Warpgate = setup.Warpgate, WarpgateMinutes = setup.WarpgateMinutes, Multiplayer = true, Mode = setup.Mode, DefenderMin = setup.DefenderMin,
            DiplomacyLocked = setup.LockDiplomacy || setup.Mode == "domination" || setup.Mode == "defender",
        };
        s.PB = new PointBuy(rules, aiData);
        Step(0.4, "Building the world");
        s.Built = WorldBuilder.Build(S, rules, models, opts, null, brushes, W => s.PlacePlayers(W, S, rules));
        var W = s.World;
        // the debug options chosen before the start (cheats.js applyCheats), then the end of the game and the brains
        Step(0.9, "Waking the computer players");
        Cheats.Apply(W, setup.Debug?.ForGame(), s.Human);
        W.GameOverInit(S.Mp.LastManStanding, S.Flags.Select(f => new FlagSpec { Name = f.Name, X = f.X, Z = f.Z, Rot = f.Rot }), setup.DefenderMin);
        var ad = new AiData(aiData);
        if (ad.ReadControlScript(aiControlScript)) L.Info("the computer players' gifts from the control script (MIRAGE: ai.json has none)");
        s.CreateBrains(ad);
        // the human's own fog of war (main.js G.sees: no allies' eyes in a skirmish); "Disable fog of war" reveals it all
        W.FogAllies = false;
        flags?.Apply(W, s.Human, W.Players.Where(p => !p.Ai));      // (PvP: every human its own fog - docs/MULTIPLAYER.md)
        if (!setup.Fog) W.SetFogRevealAll(true);
        // the camera at the human player's home (or the first player's)
        var home = s.Homes.FirstOrDefault(h => h.Owner == s.Human) ?? s.Homes.FirstOrDefault();
        double cx = home?.Pos.X ?? 0, cz = home?.Pos.Z ?? 0;
        s.StartCamera = (cx, cz, S.Kind == "original" ? Math.PI / 2 - S.DefaultCamera : SMath.Atan2(-cx, -cz));
        L.Info($"skirmish on {S.Name} ({setup.Mode}, points {setup.Points}): {string.Join(", ", W.Players.Select(p => $"{p.Id} {p.Tribe}{(p.Ai ? " ai " + s._resolved[p.Id].Behaviour + " level " + s._resolved[p.Id].Level : "")} team {p.Team} hq {p.Hq}"))}");
        Step(1, "Ready");
        return s;
    }

    /// <summary>main.js createBrains with cfg.slots: one brain per computer slot, its level and personality
    /// (ServerApp.usl:454-488), and its army for the budget: the AiPBPreset of its sub-strategy and difficulty
    /// (AIC:148-155)</summary>
    void CreateBrains(AiData data)
    {
        var W = World; var S = Map;
        var aiSlots = new List<SkirmishAiSlot>();
        for (int i = 0; i < W.Players.Count; i++)
        {
            var p = W.Players[i];
            if (p.Ai) aiSlots.Add(new SkirmishAiSlot(p, _resolved[i].Level, _resolved[i].Behaviour));
        }
        AiMapOptions? mo = null;
        if (S.AiOptions != null)
        {
            var o = new JsonObject();
            foreach (var (k, v) in S.AiOptions) o[k] = v;
            mo = AiMapOptions.From(o, new AiMapOptions { Harbour = W.WaterNav != null, Watermap = W.WaterNav != null, Walls = false, MarkplaceOutpost = false, HuntAnimals = true });
        }
        Ai = AiPlayers.ForSkirmish(W, data, aiSlots, mo, Human?.Id ?? -1).Attach();
        foreach (var b in Ai.Brains.Values)
        {
            if (!_pbStart.TryGetValue(b.P.Id, out var st)) continue;
            var pre = PB.AiPreset(b.P.Tribe, st.Credits, b.Sub ?? "G1", _resolved[b.P.Id].Level) ?? PB.Preset(b.P.Tribe, st.Credits, "standard");
            PB.Place(W, b.P, PB.Load(pre, b.P.Tribe, -1, buildings: false, mp: false), st.X, st.Z, st.Stand, st.Face);
            W.RecomputeCapsOf(b.P);
        }
        _pbStart.Clear();
    }

    readonly List<SkirmishSlot> _resolved = new();
    readonly Dictionary<int, (double Credits, double X, double Z, double Stand, double Face)> _pbStart = new();

    /// <summary>skirmish.js resolveSetup: the slots that play, each with a headquarters position (chosen, or random and
    /// distinct: ServerApp.usl:346-398), a tribe (Random: one the position allows), a behaviour (the personality;
    /// ai_Random: Dodo / Giraffe / Schnecke - never the Passive one, ServerApp.usl:441-453); Defender: the slot on
    /// head quarter 0 defends (team 1), the others attack (team 2) (ServerApp.usl:503-517)</summary>
    List<SkirmishSlot> Resolve(GameWorld W, MapSource S)
    {
        var hqs = S.StartSlots.Count > 0 ? S.StartSlots.Select(x => x ?? 0).ToList() : Enumerable.Range(0, S.Starts.Count).ToList();
        var free = hqs.ToList();
        var outp = new List<SkirmishSlot>();
        foreach (var (s0, _) in Skirmish.ActiveSlots(Setup))
        {
            var s = s0.Clone();
            int hq;
            if (Setup.ChooseHQ && hqs.Contains(s.Hq)) { hq = s.Hq; free.Remove(hq); }
            else if (free.Count > 0) { int k = W.Rng.Int(free.Count); hq = free[k]; free.RemoveAt(k); }
            else hq = hqs[outp.Count % hqs.Count];
            s.Hq = hq;
            outp.Add(s);
        }
        foreach (var s in outp)
        {
            var allowed = (S.Mp.Slots.TryGetValue(s.Hq.ToString(), out var ms) && ms.Tribes != null ? ms.Tribes : TRIBES.ToList()).Where(t => TRIBES.Contains(t)).ToList();
            if (s.Tribe == "Random" || !TRIBES.Contains(s.Tribe)) s.Tribe = allowed.Count > 0 ? allowed[W.Rng.Int(allowed.Count)] : "Hu";
            if (s.Type == "ai") s.Behaviour ??= Skirmish.BehaviourOf(s.Personality) ?? new[] { "Dodo", "Giraffe", "Schnecke" }[W.Rng.Int(3)];
            if (Setup.Mode == "defender") s.Team = s.Hq == 0 ? 1 : 2;
        }
        return outp;
    }

    /// <summary>main.js skirmishPlayers: the players on their start locations with the tribe's start (base, three
    /// workers, resources: the predefined set "_pb_locked"; an army budget: PointBuy), team, colour and handicap (the
    /// tech tree's /Filters/Special/Upgrades/handicap/handicap_&lt;hp&gt;: hit points of everything but SEAS), the map's
    /// population limit of its start slot; then the map's objects of the slots that are played and those of nobody</summary>
    void PlacePlayers(GameWorld W, MapSource S, Rules.Rules D)
    {
        var list = Resolve(W, S);
        _resolved.AddRange(list);
        W.Players.Clear();
        for (int id = 0; id < list.Count; id++)
        {
            var s = list[id];
            int n = list.Count(o => o.Tribe == s.Tribe);
            var c = Colors.ColorById(s.Color);
            var fb = Colors.PLAYER_COLORS[id % Colors.PLAYER_COLORS.Count];
            var p = new Player(id, s.Tribe, new PlayerOpts
            {
                Name = TribeName(s.Tribe) + (n > 1 ? " " + list.Take(id + 1).Count(o => o.Tribe == s.Tribe) : ""), Rules = D, Ai = s.Type == "ai", Team = s.Team,
                Gameplay = new GameplayOptions { EpochSix = Setup.EpochSix, RemoveTitans = Setup.RemoveTitans, FlyingEnabled = Setup.FlyingEnabled, Portals = Setup.Portals && !Setup.Warpgate, BuildingLimit = Setup.BuildingLimit },
                // (a custom colour "#rrggbb": ColorSlot -1, the view draws its RGB values)
                Color = c != null ? Colors.Hex(c.Dark) : Colors.Hex(fb.Dark), PartyColor = c != null ? Colors.Hex(c.Light) : null, ColorSlot = c != null ? Colors.IndexOf(c.Id) : -1,
            });
            p.Hq = s.Hq;
            p.Slot = s;
            W.Players.Add(p);
        }
        Human = W.Players.FirstOrDefault(p => !p.Ai);
        bool bought = Setup.Points >= 0 && PB.Ready;
        for (int id = 0; id < list.Count; id++)
        {
            var s = list[id]; var p = W.Players[id];
            if (Setup.Handicap && s.Hp != 100 && p.Tt != null) p.Tt.Enable($"Special/Upgrades/handicap/handicap_{s.Hp}");
            if (S.Mp.Slots.TryGetValue(s.Hq.ToString(), out var ms) && ms.PopMax is double pm && pm > 0) p.PopMax = pm;
            int i = S.StartSlots.Count > 0 ? S.StartSlots.FindIndex(x => x == s.Hq) : s.Hq;
            var pos = S.Starts[i >= 0 && i < S.Starts.Count ? i : 0];
            double bx = pos[0], bz = pos[1];
            var st = D.StartOf(p.Tribe);
            if (st == null) { L.Warn($"no start of the tribe {p.Tribe}"); Problems.Add($"no start of the tribe {p.Tribe}"); continue; }
            foreach (var (k, v) in st.Res) p.Res[k] = v;
            // the map's start resources of the slot (Restrictions/Resources; all 0 on every original map: the tribe's start)
            if (ms != null && ms.Res.Values.Any(v => v > 0)) foreach (var (k, v) in ms.Res) p.Res[k] = v;
            W.ClearArea(bx, bz, 16);
            double face = SMath.Atan2(-bx, -bz);
            // the main building; the Aje resource collector of a human player with an army budget is a card of the
            // bought army (StartLocation.usl:386-390)
            Entity? bas = null;
            if (!(bought && s.Type == "human" && p.Tribe == "Aje"))
                bas = D.Info(st.Base)?.Type == "BLDG"
                    ? W.PlaceBuilding(st.Base, p, bx, bz, Math.Round(face / (Math.PI / 4)) * (Math.PI / 4), true)
                    : W.SpawnUnit(st.Base, p, bx, bz, 1, face);
            double stand = bas != null ? bas.Radius : 3;
            if (!bought)
                for (int k = 0; k < st.Units.Count; k++)
                {
                    double a = face + Math.PI + (k - 1) * 0.5, r = stand + 5;
                    W.SpawnUnit(st.Units[k], p, bx + SMath.Sin(a) * r, bz + SMath.Cos(a) * r, 1, face);
                }
            else if (s.Type == "human")
            {
                // the army of the setup's "My Army" page, else the chosen default preset for the budget
                double total = PB.Total(Setup.Points, p.Tribe);
                var army = s.OwnArmy(Setup.Points) ? s.Army!.Clone() : PB.Load(PB.Preset(p.Tribe, Setup.Points, string.IsNullOrEmpty(s.Preset) || s.Preset == "custom" ? "standard" : s.Preset), p.Tribe, total);
                var made = PB.Place(W, p, army, bx, bz, stand, face, total, check: true);
                bas ??= made.FirstOrDefault(u => u.Name == "aje_resource_collector") ?? made.FirstOrDefault();
            }
            else _pbStart[id] = (Setup.Points, bx, bz, stand, face);          // bought when its brain starts (CreateBrains)
            if (bas != null) Homes.Add(bas);
        }
        // the map's objects of the start slots (a slot nobody plays: not placed), and those of nobody
        foreach (var o in S.Owned)
        {
            var p = o.Owner == null ? null : W.Players.FirstOrDefault(q => q.Hq == o.Owner);
            if (o.Owner != null && p == null) continue;
            try
            {
                Entity? e = o.Type == "BLDG" ? W.PlaceBuilding(o.Cls, p, o.X, o.Z, o.Rot, !(o.Attr.TryGetValue("building_ready", out var br) && br == "0"))
                    : W.Spawn(o.Cls, p, o.X, o.Z, new SpawnOpts { Level = Math.Max(o.Level, D.MinLevel(o.Cls, p)), Heading = o.Rot });
                // the map editor's hit points at the start (the remake's attribute hp_percent, docs/MAP_EDITOR.md)
                if (e != null && o.Attr.TryGetValue("hp_percent", out var hpp) && double.TryParse(hpp, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pc) && pc > 0 && pc < 100)
                    W.SetHp(e, Math.Max(1, Math.Round(e.MaxHp * pc / 100)));
            }
            catch (Exception e) { L.Warn($"map object not placed: {o.Cls} {e.Message}"); }
        }
        foreach (var p in W.Players) W.RecomputeCapsOf(p);
    }

    /// <summary>one simulation step (World.STEP = 50 ms)</summary>
    public void Step() => World.Step();
    /// <summary>the world's state hash (the brains and the game-over rules are the world's: World.StateHash)</summary>
    public ulong StateHash() => World.StateHash();
    public void Run(double seconds) { int n = (int)Math.Round(seconds / GameWorld.STEP); for (int i = 0; i < n; i++) World.Step(); }
    /// <summary>the game is over for the human player (game/systems/gameover.js: defeated or won; without a human: one
    /// side left)</summary>
    public bool Over => Human != null ? Human.Defeated || Human.Won
        : World.Go != null && World.Go.State.Count > 0 && World.Go.State.Values.Count(v => v == "alive") <= 1;
}
