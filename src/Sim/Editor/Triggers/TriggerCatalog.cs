// The trigger editor's catalogue of condition and action types (docs/MAP_EDITOR.md "Triggers", docs/LUA_MAPS.md §2):
// every type the trigger engine runs (Scripting/TriggerTypes.All - the original's 24 conditions and 50 actions, the
// MIRAGE ones, the remake's Lua Check / Script), grouped by category, each with its parameters: the key, what the
// editor offers to fill it (a number, a text, a choice, a player, a REFERENCE picked from the map - a region, an
// object, a group - or a name of the map's data - a variable, a quest, a trigger, a text key ...), the default the
// engine uses when the parameter is left out, and a line of help.
//
// The parameters and defaults are the original level editor's (TriggerCatalog.Data.cs, generated from its
// *_attrib_def.txt by tools/editor/gen_trigger_catalog.py), plus the few the remake's engine reads that the original
// has not (SetTrigger's `trigger` by name, a node's `note`). The defaults are what world.lua's trigger_defaults hold, so a
// new map's triggers run as a converted mission's (TriggerCatalog.DefaultsJson). No Godot types.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Scripting;

namespace PwRemake.Sim.Editor.Triggers;

/// <summary>what a parameter holds - the form's field for it</summary>
public enum PType
{
    Text, Int, Number, Bool, Choice,
    /// <summary>a player slot (0 - 7; -1 nobody, -2 anybody where allowed)</summary>
    Player,
    /// <summary>references picked from the map</summary>
    Region, Object, ObjectName, Group, Position, Positions,
    /// <summary>names of the map's data</summary>
    Variable, Quest, QuestName, Trigger, TriggerName, Folder, TextKey, Sequence, Dialog, QuestionMark,
    /// <summary>a class of the rules ("hu_worker"); object types ("CHTR|BLDG|")</summary>
    Class, ObjTypes,
    /// <summary>a count expression (">=3", "<1", "$(var)")</summary>
    Count,
    /// <summary>several lines / a list in the original's separators (kept as text)</summary>
    Lines,
}

public sealed record ParamSpec(string Key, PType Type, string? Default, string Help, IReadOnlyList<(string Value, string Label)>? Choices = null,
    string Section = "", bool Advanced = false)
{
    public override string ToString() => $"{Key}:{Type}{(Default != null ? "=" + Default : "")}";
}

public sealed record TypeSpec(string Code, string Name, bool IsCondition, string Category, string Summary, string Origin, IReadOnlyList<ParamSpec> Params)
{
    public ParamSpec? Param(string key) => Params.FirstOrDefault(p => p.Key == key);
    /// <summary>the defaults the engine applies (trigger_defaults of this type)</summary>
    public Dictionary<string, string> Defaults() => Params.Where(p => p.Default != null && !p.Extra()).ToDictionary(p => p.Key, p => p.Default!, StringComparer.Ordinal);
    public bool IsCode => Code is "SCRP" or "CHCK";
    public override string ToString() => $"{Name} ({Code})";
}

static class ParamSpecX
{
    /// <summary>a parameter the remake reads that the original does not define (no default in trigger_defaults)</summary>
    public static bool Extra(this ParamSpec p) => p.Section == "remake";
}

public static partial class TriggerCatalog
{
    // ------------------------------------------------------------------ categories
    static readonly (string Cat, string[] Codes)[] CondCats =
    {
        ("Time", new[] { "TIME", "RTME", "TIMR", "TRUE", "DAYN" }),
        ("Objects", new[] { "REGN", "DEAD", "DYIN", "OBJP", "ITEM", "BLDG", "SGHT", "ISFG", "WAYR" }),
        ("Groups", new[] { "CKGR", "GREG" }),
        ("Players", new[] { "PLYR", "TECH", "PLDE", "DIPL" }),
        ("Variables and quests", new[] { "CVAR", "QUES" }),
        ("Scenes", new[] { "SQEN", "DSEN" }),
        ("Tutorial", new[] { "UNTT" }),
        ("Lua", new[] { "CHCK" }),
    };
    static readonly (string Cat, string[] Codes)[] ActCats =
    {
        ("Triggers and the mission", new[] { "TRIG", "ACND", "QUIT", "GAOV", "BONI", "FDBK" }),
        ("Quests and interface", new[] { "QUES", "QMRK", "MPNG", "INBA", "TIMR", "PSND", "MUSI", "WTHR" }),
        ("Scenes and effects", new[] { "SQNZ", "DGSC", "EFCT", "SFOW" }),
        ("Objects", new[] { "COBJ", "SPGR", "CPLX", "DELO", "ACDO", "WYPT", "OBAP", "OCPY", "REPL", "TRSP", "UNIT" }),
        ("Groups", new[] { "ADGR", "RMGR" }),
        ("Variables", new[] { "VARS" }),
        ("Players", new[] { "RSRC", "DIPL", "SNFA", "TECH", "PLCP", "BLSL", "POPL" }),
        ("Regions", new[] { "ARGN", "MRGN" }),
        ("Computer players", new[] { "AIBV", "AIFT", "AIAM", "AIDA", "AILU", "AIRG", "AICM", "AIST" }),
        ("Lua", new[] { "SCRP" }),
    };

    public static IReadOnlyList<string> Categories(bool conditions) => (conditions ? CondCats : ActCats).Select(c => c.Cat).ToList();

    // ------------------------------------------------------------------ the table
    static List<TypeSpec>? s_all;
    /// <summary>every type of the engine, conditions first, in category order</summary>
    public static IReadOnlyList<TypeSpec> All => s_all ??= Build();

    public static IEnumerable<TypeSpec> Of(bool conditions) => All.Where(t => t.IsCondition == conditions);
    public static TypeSpec? Find(string code, bool condition) => All.FirstOrDefault(t => t.Code == code && t.IsCondition == condition);

    /// <summary>the types whose name, code, category or summary contain every word of the text (case ignored)</summary>
    public static List<TypeSpec> Search(string text, bool conditions)
    {
        var words = (text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Of(conditions).Where(t =>
        {
            var hay = $"{t.Name} {t.Code} {t.Category} {t.Summary}".ToLowerInvariant();
            return words.All(w => hay.Contains(w));
        }).ToList();
    }

    /// <summary>world.lua's trigger_defaults for a map that has none (a new map): { conditions = { TYPE = { k = v } }, actions = ... }</summary>
    public static JsonObject DefaultsJson()
    {
        JsonObject Part(bool c)
        {
            var o = new JsonObject();
            foreach (var t in Of(c).OrderBy(t => t.Code, StringComparer.Ordinal))
            {
                if (t.IsCode) continue;
                var d = new JsonObject();
                foreach (var (k, v) in t.Defaults()) d[k] = v;
                o[t.Code] = d;
            }
            return o;
        }
        return new JsonObject { ["conditions"] = Part(true), ["actions"] = Part(false) };
    }

    static List<TypeSpec> Build()
    {
        var res = new List<TypeSpec>();
        foreach (var cond in new[] { true, false })
            foreach (var (cat, codes) in cond ? CondCats : ActCats)
                foreach (var code in codes)
                {
                    var e = TriggerTypes.All.First(x => x.Code == code && x.IsCondition == cond);
                    var def = Defs.FirstOrDefault(d => d.Cond == cond && d.Code == code).Params;
                    // AIST is the old name of AIBV: the same parameters
                    if (def == null && code == "AIST") def = Defs.First(d => !d.Cond && d.Code == "AIBV").Params;
                    var ps = new List<ParamSpec>();
                    foreach (var (k, ty, dv) in def ?? Array.Empty<(string, string, string?)>()) ps.Add(Spec(code, cond, k, ty, dv));
                    foreach (var x in Extras(code, cond)) if (ps.All(p => p.Key != x.Key)) ps.Add(x);
                    // the order of the form: the type's own parameters first, the object queries after them
                    ps = ps.OrderBy(p => p.Section.Length == 0 ? 0 : p.Section == "remake" ? 2 : 1).ToList();
                    res.Add(new TypeSpec(code, e.Name, cond, cat, e.Summary, e.Origin, ps));
                }
        return res;
    }

    // ------------------------------------------------------------------ the parameters the engine reads beyond the original's
    static IEnumerable<ParamSpec> Extras(string code, bool cond)
    {
        const string R = "remake";
        switch (code)
        {
            case "TRIG" when !cond: yield return new ParamSpec("trigger", PType.TriggerName, null, "the trigger by its name (instead of guid)", Section: R); break;
            case "CKGR": yield return new ParamSpec("group_name", PType.Group, null, "the group by its name (when group_guid finds none)", Section: R); break;
            case "CHCK": yield return new ParamSpec("every", PType.Number, "0.5", "checked every so many seconds", Section: R); break;
            case "DAYN":
                yield return new ParamSpec("phase", PType.Choice, "night", "the phase of the map's day-night cycle (map settings: Day and night)",
                    new[] { ("day", "day"), ("dusk", "dusk"), ("night", "night"), ("dawn", "dawn") }, Section: R);
                yield return new ParamSpec("night", PType.Count, null, "optional: the night number (how many dusks have begun), a count: >=n, ==n ...", Section: R);
                break;
        }
    }

    // ------------------------------------------------------------------ one parameter: its kind, choices and help
    static readonly Regex QueryPrefix = new("^(dst_|B_|trgt_|sub_)(?=rgn_guid|obj_|exclude_class|char_|from_condition)", RegexOptions.CultureInvariant);

    static readonly (string, string)[] Owners =
    {
        ("-2", "any player"), ("-1", "nobody"), ("0", "player 0"), ("1", "player 1"), ("2", "player 2"), ("3", "player 3"),
        ("4", "player 4"), ("5", "player 5"), ("6", "player 6"), ("7", "player 7"),
    };
    static readonly (string, string)[] Players = Owners.Skip(2).ToArray();
    static readonly (string, string)[] Difficulties = { ("easy", "easy"), ("medium", "medium"), ("hard", "hard") };
    static readonly (string, string)[] OnOff = { ("1", "on"), ("0", "off") };
    static readonly (string, string)[] Relations = { ("0", "hostile"), ("1", "neutral"), ("2", "friendly") };
    static readonly (string, string)[] Tribes = { ("All", "any tribe"), ("Hu", "Norsemen"), ("Aje", "Dustriders"), ("Ninigi", "Dragon Clan"), ("SEAS", "SEAS") };
    static readonly (string, string)[] Castes = { ("All", "any caste"), ("nat", "nature"), ("res", "resource"), ("tec", "technics") };
    static readonly (string, string)[] Gaits = { ("1", "1 slowest"), ("2", "2 walk"), ("3", "3 trot"), ("4", "4 run") };

    static ParamSpec Spec(string code, bool cond, string key, string ty, string? dv)
    {
        var m = QueryPrefix.Match(key);
        string pre = m.Success ? m.Groups[1].Value : "";
        string k = key[pre.Length..];
        string section = pre switch
        {
            "dst_" => "Targets", "B_" => "Second objects (B)", "trgt_" => "Position from an object", "sub_" => "Transporters",
            _ => IsQueryKey(k) && !(code is "COBJ" or "CPLX") ? "Objects" : "",
        };
        PType t = ty switch { "int" => PType.Int, "float" => PType.Number, "bool" => PType.Bool, _ => PType.Text };
        string help = "";
        IReadOnlyList<(string, string)>? ch = null;
        bool adv = false;
        void Set(PType tt, string h, IReadOnlyList<(string, string)>? c = null) { t = tt; help = h; ch = c; }
        // the object queries (docs/LUA_MAPS.md §2.1)
        if (section.Length > 0 && pre.Length + k.Length == key.Length && IsQueryKey(k))
            switch (k)
            {
                case "rgn_guid": Set(PType.Region, "the region the objects must be in (empty / UniqueWorldRegion: the whole map)"); break;
                case "obj_guid": Set(PType.Object, "one object or group of the map (then the filters below are not used)"); break;
                case "obj_name": Set(PType.ObjectName, "an object or a group by its name"); break;
                case "obj_type": Set(PType.ObjTypes, "object types, |-separated (CHTR characters, ANML animals, VHCL vehicles, SHIP, BLDG ...; All = everything)"); break;
                case "obj_owner": Set(PType.Player, "the objects' owner", Owners); break;
                case "obj_class": Set(PType.Class, "exactly this class"); break;
                case "exclude_class": Set(PType.Class, "|-separated classes to leave out"); adv = true; break;
                case "char_tribe": Set(PType.Choice, "characters of this tribe", Tribes); adv = true; break;
                case "char_caste": Set(PType.Choice, "characters of this caste", Castes); adv = true; break;
                case "char_level": Set(PType.Int, "characters of this level (0-based; -1 any)"); adv = true; break;
                case "from_condition": Set(PType.Int, "take the objects condition number i (0-based) of this trigger found (-1: the query)"); adv = true; break;
            }
        else switch (k)
            {
                case "difficulty": Set(PType.Choice, "with \"by difficulty\": the action runs only on this difficulty", Difficulties); break;
                case "obj_count" or "check_val" or "value" when code is "REGN" or "GREG" or "CKGR" or "BLDG": Set(PType.Count, "a count: >=n, >n, <=n, <n, ==n, !=n (a bare n = >=n; n may be $(variable))"); break;
                case "attrib_value" when code == "PLYR": Set(PType.Count, "a count: >=n, >n, <n ..."); break;
                case "group" or "group_guid": Set(PType.Group, "a group of the map"); break;
                case "varname": Set(PType.Variable, "a variable of the map"); break;
                case "quest_guid": Set(PType.Quest, "a quest of the map"); break;
                case "quest_name": Set(PType.QuestName, "the quest by its name (instead of quest_guid)"); break;
                case "guid" when code == "TRIG": Set(PType.Trigger, "the trigger to switch"); break;
                case "nodename": Set(PType.Folder, "a trigger folder (path)"); break;
                case "pos" or "obj_pos" or "position" or "fow_pos" or "spawn_position" or "position_edit": Set(PType.Position, "a point of the map (click on the map)"); break;
                case "waypoints": Set(PType.Positions, "the points to walk (x y z|x y z|...)"); break;
                case "sequence" or "sequence_name": Set(PType.Sequence, "a sequence (cutscene) of the mission"); break;
                case "scene" or "dlgscene_name": Set(PType.Dialog, "a dialogue scene of the mission"); break;
                case "questionmark": Set(PType.QuestionMark, "a question mark of the map"); break;
                case "reason" or "questiontooltip" or "tooltip": Set(PType.TextKey, "a text key of the map's texts"); break;
                case "text" when code == "INBA": Set(PType.TextKey, "key<TAB>arg1<TAB>arg2 (the args may be $(variable)); empty removes the bar"); break;
                case "msg_text": Set(PType.Text, "a designer's note (written to the log)"); break;
                case "obj_name" when code is "COBJ" or "CPLX": Set(PType.Class, "the class to create"); break;
                case "obj_owner" when code is "COBJ" or "CPLX": Set(PType.Player, "the owner", Owners.Skip(1).ToArray()); break;
                case "class" or "new_obj": Set(PType.Class, "a class of the rules"); break;
                case "owner" when code == "MPNG": Set(PType.Text, "All, a player or \"trigger obj\""); break;
                case "owner" or "player" or "player_id" or "new_owner" or "plyr1" or "plyr2":
                    Set(PType.Player, "a player", code == "PSND" || code == "FDBK" || code == "GAOV" ? Owners.Skip(1).ToArray() : code == "QUES" ? Owners.Skip(1).ToArray() : Players); break;
                case "relation": Set(PType.Choice, "plyr1's relation towards plyr2", Relations); break;
                case "state" when code == "TRIG": Set(PType.Choice, "switch on, off or toggle", new[] { ("1", "on"), ("0", "off"), ("2", "toggle") }); break;
                case "deststate": Set(PType.Choice, "the folder on or off", OnOff); break;
                case "dest_state" when code == "QUES": Set(PType.Choice, cond ? "the quest's state" : "what happens to the quest",
                    cond ? new[] { ("0", "visible"), ("1", "accomplished"), ("2", "failed"), ("3", "none of them") } : new[] { ("0", "show"), ("1", "accomplished"), ("2", "failed"), ("3", "hide") }); break;
                case "dest_state" when code == "ARGN": Set(PType.Choice, "on or off", OnOff); break;
                case "sub_idx": Set(PType.Int, "the shape (0-based; -1 all shapes of the region)"); break;
                case "operation" when code == "CVAR": Set(PType.Choice, "== compares text, the others numbers", new[] { ("==", "=="), ("!=", "!="), (">", ">"), ("<", "<"), (">=", ">="), ("<=", "<="), ("=", "= (number)") }); break;
                case "operation" when code == "VARS": Set(PType.Choice, "set keeps the text; + - * / integers", new[] { ("set", "set"), ("+", "+"), ("-", "-"), ("*", "*"), ("/", "/") }); break;
                case "local": Set(PType.Choice, "a level variable or a profile variable (kept between missions)", new[] { ("1", "level"), ("0", "profile") }); break;
                case "event" when code == "TIMR": Set(PType.Choice, "what to do with the timer", new[] { ("create", "create (restart)"), ("pause", "pause"), ("unpause", "unpause"), ("kill", "kill") }); break;
                case "questionstate": Set(PType.Choice, "the question mark's look", new[] { ("STATE_INVISIBLE", "invisible"), ("QM_STATE_YELLOW", "yellow ?"), ("QM_STATE_GREEN", "green ?"), ("QM_STATE_RED", "red ?"), ("EC_STATE_YELLOW", "yellow !") }); break;
                case "colortype": Set(PType.Choice, "the marker's colour", new[] { ("SPMainQuest", "main quest"), ("SPOptQuest", "optional quest"), ("SPHint", "hint"), ("Attack", "attack"), ("FixedColor", "fixed colour") }); break;
                case "patrolmode": Set(PType.Choice, "once, in a circle, back and forth", new[] { ("0", "once"), ("1", "circle"), ("2", "back and forth") }); break;
                case "walkspeed": Set(PType.Choice, "the gait", Gaits); break;
                case "behavior": Set(PType.Choice, "the computer player's personality", new[] { ("Mikrobe", "Mikrobe (asleep)"), ("Turtle", "Turtle"), ("Dodo", "Dodo"), ("Giraffe", "Giraffe"), ("FightOnly", "FightOnly"), ("Schnecke", "Schnecke") }); break;
                case "aggro_state": Set(PType.Choice, "aggression of the units", new[] { ("0", "stand"), ("1", "defensive"), ("2", "aggressive") }); break;
                case "condition_type": Set(PType.Choice, "what the player did", new[] { ("CAME", "moved the camera"), ("USEL", "selected"), ("TRMO", "boarded") }); break;
                case "action" when code == "ACDO":
                    Set(PType.Choice, "what the objects do (additional_params: its parameters)", new[] { "WalkAction", "Aggressive Walk", "Attack", "SetPos", "RotateTo", "SetAnim", "Invulnerability", "WeaponRemove", "AbortTask", "Stop", "Kill", "FullHeal", "Open Gate", "Close Gate", "Auto Gate", "SetRallyPoint", "GiveItem", "BuildUp", "JumpOffWall", "LevelUp", "BuildDown", "entrench", "PrepareUnboard" }.Select(x => (x, x)).ToArray()); break;
                case "flags" when code == "OBAP": Set(PType.Int, "1 visible + 2 hitable + 4 selectable (+ 8 / 16 / 32 construct / destruct / deconstruct; 0 = gone; 7 = a normal unit)"); break;
                case "changes": Set(PType.Lines, "lines \"p1|relation|p2\" (one direction each)"); break;
                case "filters": Set(PType.Lines, "lines \"player|1 on / 0 off|/Filters/...\""); break;
                case "classes_0" or "classes_1" or "classes_2" or "classes_3" or "classes_4": Set(PType.Text, "\"class level count|...\" (level 1-based)"); break;
                case "res_rsrclist": Set(PType.Text, "\"food|+100|0:wood|=50|1\" = name, change, cap"); break;
                case "passengers": Set(PType.Text, "\"class/level|...\""); break;
                case "duration" when code == "TIME" && cond: Set(PType.Number, "seconds after the trigger was enabled"); break;
                case "timer_id": Set(PType.Int, "the level timer's number"); break;
                case "item_class": Set(PType.Class, "an item class (item_whatever = any)"); break;
                case "add_to_group_units" or "remove_from_group_units" or "aggro_state_units" or "objects_units" or "subjects_units" or "units":
                    Set(PType.Lines, "object GUIDs (when the query is not used)"); adv = true; break;
                case "renderable" or "renderable_type" or "num_stages" or "check_subj" or "only_fo": adv = true; break;
                default:
                    if (t == PType.Int && (k.StartsWith("check_", StringComparison.Ordinal) || k.StartsWith("use_", StringComparison.Ordinal) || k.StartsWith("enable_", StringComparison.Ordinal)
                        || k is "quit" or "snap_cam_back" or "snap_actors_back" or "disable_fow" or "ignore_pyramid" or "pos_from_obj" or "mount" or "selector_enabled"
                        or "show" or "countup" or "reset" or "repeat" or "extended" or "add_remove" or "chk_highlight" or "lock" or "all_players" or "loop" or "loop_value"))
                        t = PType.Bool;
                    break;
            }
        return new ParamSpec(key, t, dv, help, ch?.ToList(), section, adv);
    }

    static bool IsQueryKey(string k) => k is "rgn_guid" or "obj_type" or "obj_owner" or "obj_guid" or "obj_name" or "obj_class" or "exclude_class"
        or "char_tribe" or "char_caste" or "char_level" or "from_condition";
}
