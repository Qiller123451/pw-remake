// The table of every condition and action type: the original's 4-letter code, the readable name the Lua maps use,
// and the C# class (Conditions.*, Actions.*). This is the one place the names are defined: the Lua standard library
// (game/lua/std/triggers.lua) makes a constructor per entry from it, the converter (tools/prepare/campaign_to_lua.py)
// writes the same names, docs/LUA_MAPS.md lists them.
using System;
using System.Collections.Generic;
using System.Linq;

namespace PwRemake.Sim.Scripting;

public static class TriggerTypes
{
    /// <summary>One type: Code (original), Name (Lua), IsCondition / IsAction, Origin ("campaign" = the 23 + 44 of
    /// the original campaign, "mirage" = used by the MIRAGE mod's maps, "game" = defined by the original but unused,
    /// "lua" = the remake's own).</summary>
    public sealed record Entry(string Code, string Name, bool IsCondition, string Origin, string Summary);

    static readonly Dictionary<string, Func<Condition>> Cond = new(StringComparer.Ordinal)
    {
        ["TIME"] = () => new Conditions.TIME(), ["RTME"] = () => new Conditions.RTME(), ["TIMR"] = () => new Conditions.TIMR(),
        ["TRUE"] = () => new Conditions.TRUE(), ["REGN"] = () => new Conditions.REGN(), ["DEAD"] = () => new Conditions.DEAD(),
        ["DYIN"] = () => new Conditions.DYIN(), ["OBJP"] = () => new Conditions.OBJP(), ["ITEM"] = () => new Conditions.ITEM(),
        ["BLDG"] = () => new Conditions.BLDG(), ["SGHT"] = () => new Conditions.SGHT(), ["ISFG"] = () => new Conditions.ISFG(),
        ["WAYR"] = () => new Conditions.WAYR(), ["CKGR"] = () => new Conditions.CKGR(), ["UNTT"] = () => new Conditions.UNTT(),
        ["CVAR"] = () => new Conditions.CVAR(), ["QUES"] = () => new Conditions.QUES(), ["PLYR"] = () => new Conditions.PLYR(),
        ["TECH"] = () => new Conditions.TECH(), ["PLDE"] = () => new Conditions.PLDE(), ["DIPL"] = () => new Conditions.DIPL(),
        ["SQEN"] = () => new Conditions.SQEN(), ["DSEN"] = () => new Conditions.DSEN(), ["GREG"] = () => new Conditions.GREG(),
        ["CHCK"] = () => new Conditions.CHCK(),
    };

    static readonly Dictionary<string, Func<TriggerAction>> Act = new(StringComparer.Ordinal)
    {
        ["TRIG"] = () => new Actions.TRIG(), ["DGSC"] = () => new Actions.DGSC(), ["FDBK"] = () => new Actions.FDBK(), ["SQNZ"] = () => new Actions.SQNZ(),
        ["GAOV"] = () => new Actions.GAOV(), ["ACND"] = () => new Actions.ACND(), ["QUIT"] = () => new Actions.QUIT(), ["BONI"] = () => new Actions.BONI(),
        ["QUES"] = () => new Actions.QUES(), ["QMRK"] = () => new Actions.QMRK(), ["MPNG"] = () => new Actions.MPNG(), ["TIMR"] = () => new Actions.TIMR(),
        ["PSND"] = () => new Actions.PSND(), ["INBA"] = () => new Actions.INBA(), ["ACDO"] = () => new Actions.ACDO(), ["SPGR"] = () => new Actions.SPGR(),
        ["OBAP"] = () => new Actions.OBAP(), ["WYPT"] = () => new Actions.WYPT(), ["COBJ"] = () => new Actions.COBJ(), ["DELO"] = () => new Actions.DELO(),
        ["OCPY"] = () => new Actions.OCPY(), ["CPLX"] = () => new Actions.CPLX(), ["ADGR"] = () => new Actions.ADGR(), ["REPL"] = () => new Actions.REPL(),
        ["TRSP"] = () => new Actions.TRSP(), ["UNIT"] = () => new Actions.UNIT(), ["EFCT"] = () => new Actions.EFCT(), ["VARS"] = () => new Actions.VARS(),
        ["RSRC"] = () => new Actions.RSRC(), ["DIPL"] = () => new Actions.DIPL(), ["SNFA"] = () => new Actions.SNFA(), ["TECH"] = () => new Actions.TECH(),
        ["PLCP"] = () => new Actions.PLCP(), ["BLSL"] = () => new Actions.BLSL(), ["POPL"] = () => new Actions.POPL(), ["ARGN"] = () => new Actions.ARGN(),
        ["MRGN"] = () => new Actions.MRGN(), ["SFOW"] = () => new Actions.SFOW(), ["AIFT"] = () => new Actions.AIFT(), ["AIBV"] = () => new Actions.AIBV(),
        ["AIST"] = () => new Actions.AIBV(), ["AIAM"] = () => new Actions.AIAM(), ["AIDA"] = () => new Actions.AIDA(), ["AILU"] = () => new Actions.AILU(),
        ["AIRG"] = () => new Actions.AIRG(), ["AICM"] = () => new Actions.AICM(), ["MUSI"] = () => new Actions.MUSI(), ["WTHR"] = () => new Actions.WTHR(),
        ["RMGR"] = () => new Actions.RMGR(), ["SCRP"] = () => new Actions.SCRP(),
    };

    /// <summary>All types in documentation order. A code that is a condition and an action (TIMR, DIPL, TECH, QUES) has
    /// two entries; TIMR and DIPL share their readable name (the list - conditions / actions - decides).</summary>
    public static readonly IReadOnlyList<Entry> All = new List<Entry>
    {
        // ---- conditions (triggers.md §4)
        new("TIME", "Time", true, "campaign", "seconds after the trigger was enabled (repeats if the trigger is not once)"),
        new("RTME", "RandomTime", true, "campaign", "a random time between Min and Max seconds"),
        new("TIMR", "Timer", true, "campaign", "the level timer timer_id ran out"),
        new("TRUE", "True", true, "campaign", "at once when the trigger is enabled"),
        new("REGN", "Region", true, "campaign", "number of objects of a query (in a region) against obj_count"),
        new("DEAD", "Dead", true, "campaign", "an object of the query was deleted / died, or none exists"),
        new("DYIN", "Dying", true, "campaign", "an object of the query died (stays true)"),
        new("OBJP", "ObjAttrib", true, "campaign", "an attribute of the objects (hitpoints, level, GateState ...)"),
        new("ITEM", "Item", true, "campaign", "an object of the query took an item"),
        new("BLDG", "BuildingCount", true, "campaign", "finished objects of a class and owner in a region"),
        new("SGHT", "Sight", true, "campaign", "an object of query A sees one of query B"),
        new("ISFG", "IsAttacking", true, "campaign", "an object of query A attacks one of query B"),
        new("WAYR", "WaypointReached", true, "campaign", "an object of the query reached the end of its waypoints"),
        new("CKGR", "GroupCount", true, "campaign", "member count of a group against check_val"),
        new("UNTT", "Tutorial", true, "campaign", "the player moved the camera / selected / boarded (tutorial)"),
        new("CVAR", "Var", true, "campaign", "a level variable compared with a value"),
        new("QUES", "QuestState", true, "campaign", "a quest is visible / accomplished / failed / none"),
        new("PLYR", "PlayerAttrib", true, "campaign", "a player attribute (food, wood, stone, iron, units)"),
        new("TECH", "Tech", true, "campaign", "the player's tech tree has a filter (epoch reached, item used)"),
        new("PLDE", "PlayerDefeated", true, "campaign", "the player has nothing left"),
        new("DIPL", "Diplomacy", true, "campaign", "relation of plyr1 towards plyr2"),
        new("SQEN", "SequenceEnded", true, "campaign", "a sequence ended"),
        new("DSEN", "DialogEnded", true, "campaign", "a dialogue scene ended"),
        new("GREG", "GroupInRegion", true, "mirage", "members of a group found by the query, against obj_count"),
        new("CHCK", "Check", true, "lua", "a Lua function returns true"),
        // ---- actions (triggers.md §5)
        new("TRIG", "SetTrigger", false, "campaign", "enable (1) / disable (0) / toggle (2) a trigger"),
        new("DGSC", "Dialog", false, "campaign", "play a dialogue scene"),
        new("FDBK", "Feedback", false, "campaign", "a designer's debug text (log only)"),
        new("SQNZ", "Sequence", false, "campaign", "play a sequence (cutscene); quit = 1 wins the mission at its end"),
        new("GAOV", "GameOver", false, "campaign", "the mission is lost (reason text)"),
        new("ACND", "SetFolder", false, "campaign", "switch a trigger folder on / off"),
        new("QUIT", "Win", false, "campaign", "the mission is won"),
        new("BONI", "Bonus", false, "campaign", "bonus points"),
        new("QUES", "Quest", false, "campaign", "show / accomplish / fail / hide a quest"),
        new("QMRK", "QuestionMark", false, "campaign", "state and tooltip of a question mark"),
        new("MPNG", "MapPing", false, "campaign", "add / remove a minimap marker"),
        new("TIMR", "Timer", false, "campaign", "create / pause / unpause / kill a level timer"),
        new("PSND", "Sound", false, "campaign", "play a sound event"),
        new("INBA", "InfoBar", false, "campaign", "the info bar line"),
        new("ACDO", "ObjDo", false, "campaign", "the objects do something: walk, attack, teleport, animate, gates ..."),
        new("SPGR", "SpawnGroup", false, "campaign", "spawn units into a group (at a position or out of a building)"),
        new("OBAP", "ObjAppear", false, "campaign", "visible / hitable / selectable flags"),
        new("WYPT", "Waypoints", false, "campaign", "walk / patrol waypoints"),
        new("COBJ", "CreateObj", false, "campaign", "create one object"),
        new("DELO", "DeleteObj", false, "campaign", "delete objects (no death)"),
        new("OCPY", "Occupy", false, "campaign", "give objects to another player"),
        new("CPLX", "ComplexCreate", false, "campaign", "a transporter with passengers"),
        new("ADGR", "AddToGroup", false, "campaign", "add objects to a group"),
        new("REPL", "Replace", false, "campaign", "replace an object by another class"),
        new("TRSP", "Transport", false, "campaign", "board / unload transporters"),
        new("UNIT", "Unit", false, "campaign", "change hit points"),
        new("EFCT", "Effect", false, "campaign", "play a particle effect"),
        new("VARS", "SetVar", false, "campaign", "set / + / - / * / / a variable"),
        new("RSRC", "Resources", false, "campaign", "change a player's resources"),
        new("DIPL", "Diplomacy", false, "campaign", "change relations (one direction per line)"),
        new("SNFA", "NeutralToAnimals", false, "campaign", "wild animals and a player ignore each other"),
        new("TECH", "TechFilters", false, "campaign", "enable / disable tech tree filters"),
        new("PLCP", "PlayerCaps", false, "campaign", "storage limits"),
        new("BLSL", "BlockSlots", false, "campaign", "unit slots per level of the army pyramid"),
        new("POPL", "Population", false, "campaign", "population limit"),
        new("ARGN", "ActivateRegion", false, "campaign", "a region or one of its shapes on / off"),
        new("MRGN", "MoveRegion", false, "campaign", "move a region"),
        new("SFOW", "FogReveal", false, "campaign", "reveal the fog of war"),
        new("AIFT", "AiFight", false, "campaign", "a computer player attacks"),
        new("AIBV", "AiBehavior", false, "campaign", "a computer player's behaviour (Turtle, Dodo, Mikrobe ...)"),
        new("AIAM", "AiAggression", false, "campaign", "aggression mode of units (0 stand, 1 defensive, 2 aggressive)"),
        new("AIDA", "AiDefendArea", false, "campaign", "a defence area of a computer player"),
        new("AILU", "AiLockUnit", false, "campaign", "units the AI must not use"),
        new("AIRG", "AiRegion", false, "campaign", "AI influence map weight of a region"),
        new("AIST", "AiBehaviorOld", false, "game", "old name of AIBV"),
        new("AICM", "AiCommand", false, "game", "a command to an AI module"),
        new("MUSI", "Music", false, "game", "play a music track"),
        new("WTHR", "Weather", false, "game", "next weather track"),
        new("RMGR", "RemoveFromGroup", false, "mirage", "remove objects from a group"),
        new("SCRP", "Script", false, "lua", "run a Lua function"),
    };

    /// <summary>A condition object of the code, or null if the code is no condition.</summary>
    public static Condition? CreateCondition(string code) => Cond.TryGetValue(code, out var f) ? f() : null;
    public static TriggerAction? CreateAction(string code) => Act.TryGetValue(code, out var f) ? f() : null;
    public static bool IsCondition(string code) => Cond.ContainsKey(code);
    public static bool IsAction(string code) => Act.ContainsKey(code);

    /// <summary>The 23 condition and 44 action types of the original campaign (triggers.md §4, §5).</summary>
    public static IEnumerable<Entry> Campaign => All.Where(e => e.Origin == "campaign");
}
