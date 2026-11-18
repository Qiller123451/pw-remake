// What the trigger engine needs of the game: the world it acts on, the mission's presentation (quests, dialogues,
// sequences, markers, sounds), the computer players. The campaign port implements this interface on the World
// (src/Sim/Campaign); the tests implement it as FakeMissionHost. It is exactly the set of calls that the JS
// conditions and actions make on G.campaign (setup.js), the object registry (registry.js), the world
// (game/scripting.js) and the mission UI / audio / AI brains - nothing of the trigger logic itself: parameter
// parsing, count expressions, flags, the queue, timers, quests, variables and the sequence / dialogue queues stay
// in the engine (docs/TRIGGERS.md "The host interface").
//
// Conventions:
//   * objects are int ids that the host gives out (its registry records: placed objects, groups, everything
//     created at run time). An id is never reused within a mission; -1 = none. A "group" is the id of a GROU object.
//   * positions are MAP coordinates (MapPos: x east, y south, z up, metres from the map's north-west corner), as in
//     the mission data and the trigger parameters; headings are map headings (radians, the data's `rot`).
//   * players are slots 0-7; -1 = nobody. Levels: 1-based unless the name says Level0.
//   * object queries are passed as the node's parameter block (TriggerParams, defaults applied) plus a prefix
//     ("", "dst_", "B_", "trgt_", "sub_"); the host implements CObjFinder (triggers.md §2.1 / §2.2,
//     registry.js select / matches).
//   * everything the host reports back (deaths, attacks, group changes ...) goes to the TriggerEngine's
//     On...() methods (TriggerEngine.cs "events from the world"); the engine runs its queue in Update().
using System.Collections.Generic;

namespace PwRemake.Sim.Scripting;

/// <summary>What the engine reads of one object (registry record + world entity). Null from the host = the
/// object does not exist / is gone.</summary>
public sealed class ObjectInfo
{
    public int Id = -1;
    public string Guid = "", Name = "", Class = "";
    /// <summary>4-letter object type: CHTR ANML VHCL SHIP BLDG DCCO GROU QMRK ...</summary>
    public string Type = "";
    public int Owner = -1;
    /// <summary>Map position (a passenger: where its transporter is) and map heading.</summary>
    public double X, Y, Z, Rot;
    /// <summary>Has a world entity (unit / building); false = scenery, item, group or an object not in the world.</summary>
    public bool HasEntity;
    public bool Alive = true, IsUnit, IsBuilding;
    /// <summary>A building: construction finished (CurTask != "BuildUpB").</summary>
    public bool Built = true;
    public double Hp, MaxHp, Radius, Sight;
    /// <summary>1-based level of a unit; 0 for everything else.</summary>
    public int Level;
    /// <summary>Hidden by OBAP (out of the world until it appears).</summary>
    public bool Parked;
    /// <summary>The transporter it sits in, or -1.</summary>
    public int Inside = -1;
    public int Passengers, Capacity;
    public string? GateState, Task;
    /// <summary>Counts in the unit limit (PLDE check_pyramid).</summary>
    public bool CountsInPop;
    /// <summary>Can produce units (a Build action of its tribe makes characters / animals / vehicles / ships: PLDE).</summary>
    public bool Produces;
    /// <summary>A wall, gate or trap (PLDE check_bldgs ignores them).</summary>
    public bool WallLike;
    /// <summary>An item lying on the map (ACDO GiveItem targets).</summary>
    public bool IsItem;
    /// <summary>Class of the item it carries, or null.</summary>
    public string? CarriedItem;
}

/// <summary>Options of IMissionHost.Spawn (setup.js spawn opts).</summary>
public sealed class SpawnOptions
{
    /// <summary>1-based level; null = the class's lowest.</summary>
    public int? Level;
    /// <summary>Map heading.</summary>
    public double Rot;
    /// <summary>Do not look for a free spot (REPL of scenery, spawn out of a building).</summary>
    public bool Exact;
    /// <summary>Height for scenery put where another stood (REPL), or null.</summary>
    public double? Height;
    /// <summary>Group the new object joins, or -1.</summary>
    public int Group = -1;
    /// <summary>REPL: an object replaced by another keeps its owner as it is (no co-op seat mapping, no scaling)</summary>
    public bool Replace;
    /// <summary>no co-op enemy scaling for this one (the passengers of a CPLX transporter: its capacity is the mission's)</summary>
    public bool NoScale;
}

/// <summary>A minimap marker (MPNG, AF:2919-3062).</summary>
public sealed class MapMarker
{
    public string Id = "", Kind = "FixedColor";
    /// <summary>0xRRGGBB.</summary>
    public int Color;
    public bool Extended;
    /// <summary>Seconds; 0 = permanent.</summary>
    public double Ttl, Interval;
    public int Repeats;
    public double X, Y;
    /// <summary>The object it marks, or -1.</summary>
    public int Obj = -1;
}

/// <summary>A sequence to play (SQNZ, AF:1976-2095).</summary>
public sealed class SequenceRequest
{
    public string Path = "", Reason = "";
    public bool Quit, SnapBack, SnapActors, Fow;
    public MapPos? Camera, FowPos;
    public double FowRadius = 30;
    public string Trigger = "";
}

/// <summary>An attack order for a computer player (AIFT custom attack, AF:4463-4641).</summary>
public sealed class AiAttackOrder
{
    public string Type = "", Behaviour = "";
    public List<int> Targets = new();
    public MapPos? Position, SpawnPosition;
    public bool AttackOnTheWay, Spawn, IgnoreLocations, TargetOnly, Ship, ShipLand;
}

/// <summary>The game as the trigger engine sees it. Every method must tolerate ids of objects that are gone
/// (do nothing / return null) and must not throw for data problems (log and go on).</summary>
public interface IMissionHost
{
    // ---------------------------------------------------------------- mission
    /// <summary>The campaign difficulty: 0 easy, 1 medium, 2 hard (Base/CurrentDifficulty).</summary>
    int Difficulty { get; }
    /// <summary>The human player's slot (0 in every original mission).</summary>
    int HumanSlot { get; }
    /// <summary>A random number in [0, 1) from the world's deterministic generator (RTME, random triggers).</summary>
    double Random();
    /// <summary>Does an object of player `actual` count for a condition about player `query`? (A co-op campaign game:
    /// "the player" is every seat, docs/COOP.md.)</summary>
    bool OwnerMatches(int actual, int query) => actual == query;
    /// <summary>Is the slot played by a human (the human player; a co-op game: every human seat)? Sounds, markers.</summary>
    bool IsHumanSide(int slot) => slot == HumanSlot;

    // ---------------------------------------------------------------- object queries (triggers.md §2, registry.js)
    /// <summary>CObjFinder.MakeQuery: live objects of the query block with the prefix, in creation order; a group
    /// named or found is replaced by its members (§2.1).</summary>
    IReadOnlyList<int> Select(TriggerParams p, string prefix);
    /// <summary>CObjFinder.Contains: does this object match the query (§2.2: a named group does not match its
    /// members, "AllNC" is no wildcard).</summary>
    bool Matches(int obj, TriggerParams p, string prefix);
    /// <summary>The object, or null if it does not exist / is gone.</summary>
    ObjectInfo? Info(int obj);
    /// <summary>A raw attribute of the object's data (objects[].attr) as string, or null (OBJP of other attributes).</summary>
    string? DataAttribute(int obj, string name);
    /// <summary>A live object by GUID, else by name; -1 if none (GUID lists of ADGR / TRSP / AILU / AIAM).</summary>
    int FindObject(string guidOrName);
    /// <summary>True for "UniqueWorldRegion", an empty or an unknown region GUID (= the whole map).</summary>
    bool IsWorldRegion(string? rgnGuid);
    /// <summary>The mission is played with another tribe than the data's and that tribe has nothing in the role of
    /// class `cls` for player `owner` (tribes.js missing()): conditions on it count as met.</summary>
    bool IsMissingClass(string cls, int owner);
    /// <summary>Finished objects of class `cls` (case-insensitive, tribe swap applied) of `owner` in the region
    /// (BLDG condition, CF:2983-3149).</summary>
    int CountFinished(string cls, int owner, string? rgnGuid);
    /// <summary>All live units (not hidden) and buildings of a player (PLDE).</summary>
    IReadOnlyList<int> PlayerObjects(int slot);
    /// <summary>Characters (CHTR) of the player at that 1-based level (SPGR checkpyramid).</summary>
    int CountCharacters(int slot, int level);
    /// <summary>CheckUnits(owner, level): the player has a free slot of the army pyramid for a 1-based level.</summary>
    bool UnitSlotFree(int slot, int level);

    // ---------------------------------------------------------------- groups (GroupObj.usl, §6.6)
    /// <summary>A group by GUID or name, -1 if none.</summary>
    int FindGroup(string? guidOrName);
    /// <summary>Live members in the order of joining.</summary>
    IReadOnlyList<int> GroupMembers(int group);
    /// <summary>The group the object is in, or -1.</summary>
    int GroupOf(int obj);
    /// <summary>Adds (a unit is in at most one group; at most 140; a group cannot hold a group). Reports
    /// TriggerEngine.OnGroupChanged.</summary>
    bool AddToGroup(int group, int obj);
    bool RemoveFromGroup(int obj);

    // ---------------------------------------------------------------- creating, deleting, changing objects
    /// <summary>Creates an object of the class (a unit, a building - finished -, else scenery with that model) at a
    /// map position; returns its id or -1 (setup.js spawn).</summary>
    int Spawn(string cls, int owner, double x, double y, SpawnOptions opts);
    /// <summary>SPGR out of a building (MiscObj.usl CDelayedArmySpawn): one unit of `cls` comes out of `source` (at its
    /// link Spwn / exit Ex_1) and walks to `target` (null: stays at the exit); joins `group`. Returns its id or -1.</summary>
    int SpawnOutOf(int source, string cls, int level, int owner, int group, MapPos? target);
    /// <summary>"class§buildup": the object's own tech tree gets that upgrade (AF:4928-4938).</summary>
    void ApplyBuildup(int obj, string buildup);
    /// <summary>Deletes without death (DELO, REPL): reports TriggerEngine.OnObjectRemoved.</summary>
    bool Remove(int obj);
    /// <summary>A normal death (ACDO Kill): a hidden object appears first, invulnerability ends; scenery is removed.</summary>
    void Kill(int obj);
    /// <summary>OCPY: new owner (-1 nobody); what the human player gets becomes selectable and is highlighted for
    /// `highlightSeconds` (0 = no).</summary>
    void SetOwner(int obj, int slot, double highlightSeconds);
    /// <summary>OBAP: 1 visible, 2 hitable, 4 selectable, 8 constructible, 16 destructible, 32 deconstructible.</summary>
    void SetAppearance(int obj, int flags);
    void SetHitpoints(int obj, double hp);
    /// <summary>Sets a unit's 1-based level.</summary>
    void SetLevel(int obj, int level);

    // ---------------------------------------------------------------- orders (ACDO, WYPT, TRSP; FightingObj)
    /// <summary>Walk to the point (WalkAction: attackMove false, slow = speed &lt; 3) or attack-move (Aggressive Walk).
    /// A unit inside a transporter gets out first; a patrol of the unit ends. Zeppelins fly at the point's height.</summary>
    void OrderMove(int obj, MapPos to, double speed, bool attackMove);
    void OrderAttack(int obj, int target);
    /// <summary>AbortTask / Stop / RotateTo's break.</summary>
    void OrderStop(int obj);
    /// <summary>A worker builds / finishes the building (ACDO BuildUp).</summary>
    void OrderBuild(int worker, int building);
    /// <summary>Board the transporter (TRSP mount).</summary>
    void OrderBoard(int unit, int transporter);
    bool CanBoard(int unit, int transporter);
    /// <summary>The transporter unloads all passengers (TRSP mount = 0).</summary>
    void UnloadAll(int transporter);
    /// <summary>Puts a unit into a transporter at once (CPLX passengers).</summary>
    void EnterTransport(int unit, int transporter);
    /// <summary>Teleport (SetPos): breaks the task.</summary>
    void Teleport(int obj, double x, double y);
    /// <summary>Break the task and face the point (RotateTo).</summary>
    void FaceTo(int obj, double x, double y);
    /// <summary>Play an animation `loops` times (SetAnim); a scenery gate's open / close also switches its
    /// blocking. False if the model has no such animation (the host may remember the wish).</summary>
    bool PlayAnim(int obj, string name, int loops);
    /// <summary>Length of an animation of the object's model in seconds, or -1 if it has none.</summary>
    double AnimLength(int obj, string name);
    /// <summary>The level designer's invulnerability; seconds &gt; 0 = for that long.</summary>
    void SetInvulnerable(int obj, bool on, double seconds);
    /// <summary>MIRAGE WeaponRemove: disarmed; seconds &gt; 0 = for that long.</summary>
    void SetDisarmed(int obj, bool on, double seconds);
    /// <summary>Gate command: 0 open, 1 close, 2 auto (a scenery gate: its animation and blocking).</summary>
    void SetGate(int obj, int state);
    /// <summary>Rally point of a building (ignored for anything else).</summary>
    void SetRallyPoint(int obj, double x, double y);
    /// <summary>The unit takes the item object (GiveItem; a carrier of the item drops it first).</summary>
    void GiveItem(int unit, int item);
    /// <summary>Unit-specific gameplay commands of ACDO without parameters: "BuildDown", "entrench", "JumpOffWall"
    /// (the Scorpion of mission 16 wakes up), "LevelUp". Returns false if the object cannot do it.</summary>
    bool Command(int obj, string command);
    /// <summary>MIRAGE PrepareUnboard: sail to the point and unload there.</summary>
    void PrepareUnboard(int obj, MapPos at);
    /// <summary>WYPT: walk the points (mode 0 once, 1 circle, 2 back and forth); straight = no aggression walk. The
    /// end of a mode-0 path is reported by TriggerEngine.OnWaypointReached. Group members are passed one by one.</summary>
    void StartPatrol(int obj, IReadOnlyList<MapPos> points, int mode, double speed, bool straight);

    // ---------------------------------------------------------------- players and the world
    bool PlayerExists(int slot);
    /// <summary>Relation of a towards b: 0 hostile, 1 neutral, 2 friendly; null if a player does not exist.</summary>
    int? Relation(int a, int b);
    /// <summary>One direction (DIPL action); the engine pushes its own DIPL conditions afterwards.</summary>
    void SetRelation(int a, int b, int relation);
    /// <summary>food, wood, stone, iron (= skulls), units, max_food / rescap_food ...; null = unknown.</summary>
    double? PlayerAttribute(int slot, string name);
    /// <summary>RSRC: mod "+n", "-n", "=n"; cap = limit to the storage (not for iron); never below 0.</summary>
    void ChangeResource(int slot, string name, string mod, bool cap);
    /// <summary>TECH condition: the player's tech tree has the filter (tribe swap applied by the host; a filter of a
    /// role the chosen tribe does not have counts as present). Null = cannot tell (no player / tech tree).</summary>
    bool? HasTechFilter(int slot, string filter);
    /// <summary>TECH action: enable / disable a filter path on the player's tech tree (tribe swap by the host).</summary>
    void SetTechFilter(int slot, string path, bool on);
    /// <summary>a TECH action's lines begin (true) / end (false): the host may rebuild each player's tech tree once at the
    /// end instead of once per filter (the same tree: it depends only on the filters switched on)</summary>
    void TechBatch(bool begin) { }
    /// <summary>the objects of one giving action (OCPY, SPGR, CPLX passengers) begin (true) / end: co-op deals them over
    /// the seats as a group</summary>
    void GiftBatch(bool begin) { }
    void SetPopulationLimit(int slot, int limit);
    /// <summary>BLSL: unit slots of the army pyramid per level 1-5.</summary>
    void SetUnitLimits(int slot, int[] perLevel);
    /// <summary>PLCP: storage limits (the strings of the data).</summary>
    void SetResourceCaps(int slot, string food, string wood, string stone);
    /// <summary>SNFA: bit 1 nest animals ignore the player, bit 2 the player's units ignore wild animals.</summary>
    void SetNeutralToAnimals(int slot, int value);
    /// <summary>ARGN: the region (or its shape subIndex &gt;= 0) on / off; false if no such region.</summary>
    bool SetRegionEnabled(string rgnGuid, bool on, int subIndex);
    bool MoveRegion(string rgnGuid, double x, double y);
    /// <summary>Switches on the shapes marked "not enabled" of a region a trigger names (engine.js fixRegions);
    /// false if the region is the world, unknown or has no such shape.</summary>
    bool EnableDisabledShapes(string rgnGuid);
    /// <summary>SFOW: reveal a circle for the player (follow = an object it moves with, or -1; seconds 0 = for
    /// good). Returns a reveal id or -1.</summary>
    int RevealArea(int slot, double x, double y, double radius, double seconds, int follow);

    // ---------------------------------------------------------------- computer players (ai.md §13.1)
    /// <summary>False if the player has no brain.</summary>
    bool AiBehavior(int slot, string behavior, string module);
    bool AiAttack(int slot, AiAttackOrder order);
    void AiAutoAttack(int slot, IReadOnlyList<int> targets);
    void AiDefendArea(int slot, string id, MapPos pos, double radius, int maxUnits);
    void AiLockUnits(int slot, IReadOnlyList<int> units, bool locked);
    /// <summary>AIAM: /AggroState_n (0 stand ground, 1 defensive, 2 aggressive).</summary>
    void SetAggression(IReadOnlyList<int> units, int state);
    /// <summary>AIRG: slot -1 = every computer player.</summary>
    void AiRegionMap(int slot, string map, string region, double value, bool add);
    void AiModuleCommand(int slot, string module, string command);
    /// <summary>GAOV: the computer players are told that the game is over.</summary>
    void AiGameOver();

    // ---------------------------------------------------------------- presentation (the mission UI, audio)
    /// <summary>A quest changed: "shown", "done", "failed", "hidden" (news ticker, quest log).</summary>
    void QuestChanged(QuestState quest, string change);
    void SetQuestionMark(string guid, string state, string? tooltip);
    void ShowMarker(MapMarker m);
    void RemoveMarker(string id);
    /// <summary>The resolved info bar line; "" removes it.</summary>
    void SetInfoBar(string text);
    /// <summary>A sound event for the human player, at a map position or null (2D).</summary>
    void PlaySound(string name, MapPos? at);
    void PlayMusic(string file, bool loop);
    /// <summary>WTHR chgtype != 0: the next track of the level's weather program.</summary>
    void NextWeather();
    void PlayEffect(string path, double x, double y, int loops);
    /// <summary>The mission UI is busy (a sequence waits until it is not: ui/mission.js busy()).</summary>
    bool IsBusy { get; }
    /// <summary>Starts a sequence (reveal, actors, the UI). True = the host plays it and calls
    /// TriggerEngine.EndSequence() when it is over or skipped; false = no file / no UI: it ends at once.</summary>
    bool PlaySequence(SequenceRequest r);
    /// <summary>The sequence has ended (also when PlaySequence returned false): actors stay / snap back, the reveal
    /// goes, the camera goes to r.Camera unless r.SnapBack.</summary>
    void SequenceFinished(SequenceRequest r);
    /// <summary>Queues a dialogue scene; true = the UI plays it and calls TriggerEngine.EndDialog(token) at its
    /// end; false = no UI (it ends in the next step).</summary>
    bool PlayDialog(string path, int token);
    /// <summary>The mission is over (QUIT, SQNZ quit, GAOV 4 s later).</summary>
    void EndMission(bool won, string text);
    /// <summary>UNTT: camera (map x, y, distance, yaw) and whether the object is selected by the player.</summary>
    (double X, double Y, double Dist, double Yaw)? Camera { get; }
    bool IsSelected(int obj);

    // ---------------------------------------------------------------- profile (kept between missions)
    /// <summary>The player's profile variables (Server/settings/ProfileVariables.txt values), or null.</summary>
    IReadOnlyDictionary<string, string>? LoadProfile();
    void SaveProfile(IReadOnlyDictionary<string, string> values);
}
