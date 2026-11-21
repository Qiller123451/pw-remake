// The map editor's players and map settings (docs/MAP_EDITOR.md "Players and map settings", plan phase 4):
//   Tools window (the Players group): the map's settings - name, author, description per language, game types, the
//            time of day, weather and wind, the atmosphere (ambience and music), the camera's angle at the start, the
//            preview picture made from the terrain; the players the map is for (= its start locations)
//   Properties: the player slots 1 - 8 - each one's start location (place it, or drag it on the map: the object tool
//            picks only start locations in this group), the tribes it may play and the one it starts as, team, colour,
//            who plays it (human, computer, either), start resources, population limit, the main building, its
//            diplomacy towards the others (missions) - and the game's defaults: the army budget, the head quarters choice,
//            MIRAGE's game options (GameplayOptions)
// Every change is one undo step (PlayersEdit / SettingsEdit). Test map plays these players (PlayersSetup.Make).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.UI;
using SimColors = PwRemake.Sim.Rules.Colors;

namespace PwRemake.Editor;

public partial class MapEditor
{
    bool _playersUiBusy;
    static string s_lang = "uk";
    static readonly string[] LANGS = { "uk", "de", "fr", "it", "es", "pl", "ru", "cz", "hu" };

    void EditPlayers(string what, Action<MapPlayerSettings> f)
    {
        _playersUiBusy = true;
        try { History.Execute(PlayersEdit.Make(Doc, f, what)); }
        finally { _playersUiBusy = false; }
    }

    void EditSettings(string what, Action<MapSettings> f)
    {
        _playersUiBusy = true;
        try { History.Execute(SettingsEdit.Make(Doc, f, what)); }
        finally { _playersUiBusy = false; }
    }

    static ScrollContainer Scroll(Control body, float h)
    {
        var s = new ScrollContainer { CustomMinimumSize = new Vector2(0, h), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        s.AddChild(body);
        return s;
    }

    HSlider Slide(double min, double max, double step, double value, Action<double> show, Action<double> commit)
    {
        var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(120, 22), FocusMode = Control.FocusModeEnum.None };
        s.ValueChanged += v => show(v);
        s.DragEnded += changed => { if (changed) commit(s.Value); };
        show(value);
        return s;
    }

    // ================================================================================ map settings (Tools window)

    void BuildPlayersPalette()
    {
        var S = Doc.GetSettings();
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        _palette.AddChild(Scroll(box, 470));
        box.AddChild(Caption("Map settings"));
        LineEdit Text(string value, Action<string> commit, string? ph = null)
        {
            var l = new LineEdit { Text = value, PlaceholderText = ph ?? "" };
            void Go() { if (l.Text != value) { value = l.Text; commit(l.Text); } }
            l.TextSubmitted += _ => Go(); l.FocusExited += Go;
            return l;
        }
        box.AddChild(Row("Name", Text(S.Name, t => EditSettings("Rename map", s => s.Name = t.Trim().Length > 0 ? t.Trim() : s.Name)), 64));
        box.AddChild(Row("Author", Text(S.Author, t => EditSettings("Change author", s => s.Author = t.Trim())), 64));
        // the description of a language
        box.AddChild(Row("Language", Options(LANGS, Array.IndexOf(LANGS, s_lang), i => { s_lang = LANGS[i]; BuildPalette(); }), 64));
        var desc = new TextEdit { Text = S.Descriptions.TryGetValue(s_lang, out var dt) ? dt : "", CustomMinimumSize = new Vector2(200, 70), WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "The map's description in this language" };
        string lang = s_lang, before = desc.Text;
        desc.FocusExited += () =>
        {
            if (desc.Text == before) return;
            before = desc.Text;
            var text = desc.Text.Trim();
            EditSettings("Change description", s => { if (text.Length > 0) s.Descriptions[lang] = text; else s.Descriptions.Remove(lang); });
        };
        box.AddChild(desc);
        // game types
        var gt = new HBoxContainer();
        foreach (var t in MapSettings.GAME_TYPES)
        {
            var cb = new CheckBox { Text = t, ButtonPressed = S.GameTypes.Contains(t), FocusMode = Control.FocusModeEnum.None };
            string tt = t;
            cb.Toggled += on => EditSettings("Change game types", s => { s.GameTypes.Remove(tt); if (on) s.GameTypes.Add(tt); s.GameTypes = MapSettings.GAME_TYPES.Where(s.GameTypes.Contains).ToList(); if (s.GameTypes.Count == 0) s.GameTypes.Add("Deathmatch"); });
            gt.AddChild(cb);
        }
        box.AddChild(gt);
        var lms = new CheckBox { Text = "Last man standing", ButtonPressed = S.LastManStanding, FocusMode = Control.FocusModeEnum.None };
        lms.Toggled += on => EditSettings("Change game rules", s => s.LastManStanding = on);
        box.AddChild(lms);
        // time, weather, atmosphere, camera
        var timeL = new Label();
        box.AddChild(timeL);
        box.AddChild(Slide(0, 24, 0.25, S.StartTime / 3600, v => timeL.Text = $"Time of day: {MapSettings.TimeText(v * 3600)}", v => EditSettings("Change time of day", s => s.StartTime = v * 3600)));
        // the remake's day-night cycle (map.lua day_night, docs/MAP_FORMAT.md): a real cycle of day, dusk, night and dawn
        var dn = new CheckBox { Text = "Day and night (a real cycle)", ButtonPressed = S.DayNight != null, FocusMode = Control.FocusModeEnum.None };
        dn.TooltipText = "The light changes from day to night and back, the lamps are lit at night, the units see less; triggers hear it (DayPhase, api.daynight)";
        dn.Toggled += on => EditSettings(on ? "Day and night on" : "Day and night off", s => s.DayNight = on ? new PwRemake.Sim.World.DayNightSpec() : null);
        box.AddChild(dn);
        if (S.DayNight is { } cyc)
        {
            void Len(string what, double min, double max, double step, double v, Func<double, string> text, Action<PwRemake.Sim.World.DayNightSpec, double> set)
            {
                var l = new Label();
                box.AddChild(l);
                box.AddChild(Slide(min, max, step, v, x => l.Text = $"{what}: {text(x)}", x => EditSettings("Change day and night", s => { if (s.DayNight != null) set(s.DayNight, x); })));
            }
            static string Mins(double s) => $"{(int)s / 60}:{(int)s % 60:00}";
            Len("Day", 30, 1200, 30, cyc.Day, Mins, (c, x) => c.Day = x);
            Len("Dusk", 0, 180, 10, cyc.Dusk, Mins, (c, x) => c.Dusk = x);
            Len("Night", 30, 900, 30, cyc.Night, Mins, (c, x) => c.Night = x);
            Len("Dawn", 0, 180, 10, cyc.Dawn, Mins, (c, x) => c.Dawn = x);
            Len("First day", 0, 1800, 30, cyc.FirstDay, x => x <= 0 ? "as every day" : Mins(x), (c, x) => c.FirstDay = x);
            Len("Sight at night", 0.3, 1, 0.05, cyc.NightSight, x => $"{x * 100:0} %", (c, x) => c.NightSight = x);
        }
        box.AddChild(Row("Weather", Options(MapSettings.WEATHER, S.Weather, i => EditSettings("Change weather", s => s.Weather = i)), 64));
        if (S.Weather != 0)
        {
            var inL = new Label();
            box.AddChild(inL);
            box.AddChild(Slide(1, 16, 1, S.WeatherIntensity, v => inL.Text = $"Intensity: {v:0}", v => EditSettings("Change weather", s => s.WeatherIntensity = v)));
            string[] dirs = { "North", "North-west", "West", "South-west", "South", "South-east", "East", "North-east" };
            box.AddChild(Row("Wind from", Options(dirs, S.WindDir, i => EditSettings("Change wind", s => s.WindDir = i)), 64));
            var wsL = new Label();
            box.AddChild(wsL);
            box.AddChild(Slide(0, 10, 1, S.WindStrength, v => wsL.Text = $"Wind: {v:0}", v => EditSettings("Change wind", s => s.WindStrength = v)));
        }
        var atmos = MapSettings.ATMOS.ToList();
        if (S.Atmos.Length > 0 && !atmos.Contains(S.Atmos)) atmos.Insert(0, S.Atmos);
        box.AddChild(Row("Atmosphere", Options(atmos.Select(a => a.Replace(".txt", "")), Math.Max(-1, atmos.IndexOf(S.Atmos)), i => EditSettings("Change atmosphere", s => s.Atmos = atmos[i])), 64));
        var camL = new Label();
        box.AddChild(camL);
        box.AddChild(Slide(0, 360, 5, S.DefaultCamera * 180 / Math.PI, v => camL.Text = $"Camera at the start: {v:0}°", v => EditSettings("Change camera", s => s.DefaultCamera = v * Math.PI / 180)));
        // the preview
        box.AddChild(new HSeparator());
        var prev = new TextureRect { CustomMinimumSize = new Vector2(120, 120), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
        if (S.Preview is PwRemake.Sim.Maps.MapPreview p && p.Rgba.Length == p.W * p.H * 4) prev.Texture = ImageTexture.CreateFromImage(Image.CreateFromData(p.W, p.H, false, Image.Format.Rgba8, p.Rgba));
        box.AddChild(prev);
        box.AddChild(PwTheme.Btn("Make the preview", () => EditSettings("Make the preview", s => s.Preview = MapPreviewMaker.Make(Doc)), 200));
        box.AddChild(PwTheme.Para($"Players: {Doc.MaxPlayers} (its start locations)", 12));
    }

    // ================================================================================ the player slots (Properties)

    internal void BuildPlayersProps()
    {
        var P = Doc.GetPlayers();
        var cfg = EditorSession.Config;
        int si = Math.Clamp(ObjectToolState.Slot, 0, MapPlayerSettings.SLOTS - 1);
        var slot = P.Slots[si];
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        _props.AddChild(Scroll(box, 320));
        // the slot buttons in their colours (a dot: it has a start location)
        var active = PlayersSetup.ActiveSlots(Doc);
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 2);
        for (int i = 0; i < MapPlayerSettings.SLOTS; i++)
        {
            int k = i;
            var b = new Button { Text = (i + 1) + (active.Contains(i) ? "•" : ""), ToggleMode = true, ButtonPressed = i == si, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(30, 26), TooltipText = $"{P.Slots[i].Name}{(active.Contains(i) ? "" : " (no start location: does not play)")}" };
            b.AddThemeColorOverride("font_color", SlotColorOf(i));
            b.AddThemeColorOverride("font_pressed_color", SlotColorOf(i));
            b.Pressed += () => { ObjectToolState.Slot = k; BuildProperties(); };
            bar.AddChild(b);
        }
        box.AddChild(bar);
        // its start location
        var st = Doc.Objects.FirstOrDefault(o => o.Type == "SLOC" && o.Owner == si);
        var stRow = new HBoxContainer();
        stRow.AddChild(PwTheme.Para(st != null ? $"Start: x {st.X:0} y {st.Y:0}" : "No start location", 13, PwTheme.Text));
        if (st != null) stRow.AddChild(PwTheme.Btn("Show", () => { var (x, z) = _src!.ToGame(st.X, st.Y); _cam!.X = x; _cam.Z = z; Sel.Clear(); Sel.Add(st); BuildProperties(); }, 64));
        else stRow.AddChild(PwTheme.Btn("Place", () => { if (ObjectToolState.EntryOf("startlocation") is { } e) StartPlacing(e); }, 64));
        box.AddChild(stRow);
        var nm = new LineEdit { Text = slot.Name };
        void Rename() { if (nm.Text.Trim() != slot.Name && nm.Text.Trim().Length > 0) EditPlayers("Rename player", p => p.Slots[si].Name = nm.Text.Trim()); }
        nm.TextSubmitted += _ => Rename(); nm.FocusExited += Rename;
        box.AddChild(Row("Name", nm, 84));
        // tribes
        var tribes = MapPlayerSettings.TRIBES.ToList();
        var allowed = new HBoxContainer();
        foreach (var t in tribes)
        {
            string tt = t;
            var cb = new CheckBox { Text = t, ButtonPressed = slot.Tribes.Count == 0 || slot.Tribes.Contains(t), FocusMode = Control.FocusModeEnum.None, TooltipText = $"{TribeInfo.Name(t)} may be played here{(cfg.PlayableTribes.Contains(t) ? "" : " (not playable in this configuration)")}" };
            cb.Toggled += on => EditPlayers("Change tribes", p =>
            {
                var l = p.Slots[si].Tribes.Count == 0 ? tribes.ToList() : p.Slots[si].Tribes.ToList();
                l.Remove(tt); if (on) l.Add(tt);
                l = tribes.Where(l.Contains).ToList();
                p.Slots[si].Tribes = l.Count == tribes.Count || l.Count == 0 ? new List<string>() : l;
            });
            allowed.AddChild(cb);
        }
        box.AddChild(allowed);
        var starts = new List<string> { "Random" }.Concat(tribes).ToList();
        box.AddChild(Row("Starts as", Options(starts.Select(t => t == "Random" ? "Random" : TribeInfo.Name(t)), starts.IndexOf(slot.Tribe), i => EditPlayers("Change tribe", p => p.Slots[si].Tribe = starts[i])), 84));
        box.AddChild(Row("Team", Spin(1, 8, 1, slot.Team, v => EditPlayers("Change team", p => p.Slots[si].Team = (int)v)), 84));
        var cols = SimColors.PLAYER_COLORS.ToList();
        box.AddChild(Row("Colour", Options(cols.Select(c => c.Name), cols.FindIndex(c => c.Id == slot.Color), i => EditPlayers("Change colour", p => p.Slots[si].Color = cols[i].Id)), 84));
        box.AddChild(Row("Played by", Options(new[] { "a human", "the computer", "either" }, Array.IndexOf(MapPlayerSettings.CONTROLS, slot.Control), i => EditPlayers("Change player", p => p.Slots[si].Control = MapPlayerSettings.CONTROLS[i])), 84));
        // start
        box.AddChild(Caption("Start"));
        box.AddChild(Row("Food", Spin(0, 100000, 50, slot.Food, v => EditPlayers("Change start resources", p => p.Slots[si].Food = v)), 84));
        box.AddChild(Row("Wood", Spin(0, 100000, 50, slot.Wood, v => EditPlayers("Change start resources", p => p.Slots[si].Wood = v)), 84));
        box.AddChild(Row("Stone", Spin(0, 100000, 50, slot.Stone, v => EditPlayers("Change start resources", p => p.Slots[si].Stone = v)), 84));
        box.AddChild(Row("Skulls", Spin(0, 100000, 10, slot.Skulls, v => EditPlayers("Change start resources", p => p.Slots[si].Skulls = v)), 84));
        box.AddChild(PwTheme.Para("All 0: the tribe's own start resources.", 11));
        box.AddChild(Row("Population", Spin(0, 1000, 1, slot.PopMax, v => EditPlayers("Change population", p => p.Slots[si].PopMax = v)), 84));
        var inc = new CheckBox { Text = "Main building at the start", ButtonPressed = slot.IncludeBuildings, FocusMode = Control.FocusModeEnum.None };
        inc.Toggled += on => EditPlayers("Change start", p => p.Slots[si].IncludeBuildings = on);
        box.AddChild(inc);
        // diplomacy
        box.AddChild(Caption("Diplomacy (missions)"));
        string[] rels = { "hostile", "neutral", "friendly" };
        for (int j = 0; j < MapPlayerSettings.SLOTS; j++)
        {
            if (j == si || (!active.Contains(j) && !active.Contains(si))) continue;
            int jj = j;
            box.AddChild(Row($"to {j + 1}", Options(rels, slot.Diplomacy[j], i => EditPlayers("Change diplomacy", p => p.SetRelation(si, jj, i))), 84));
        }
        box.AddChild(PwTheme.Btn("From the teams", () => EditPlayers("Diplomacy from the teams", p => p.DiplomacyFromTeams()), 200));
        // the game's defaults
        box.AddChild(Caption("Game"));
        double[] pts = Skirmish.POINTS;
        box.AddChild(Row("Army", Options(pts.Select(x => x < 0 ? "Predefined" : $"{x:0} points"), Array.IndexOf(pts, P.Points), i => EditPlayers("Change army", p => p.Points = pts[i])), 84));
        CheckBox Opt(string text, bool v, Action<MapPlayerSettings, bool> set, string tip)
        {
            var cb = new CheckBox { Text = text, ButtonPressed = v, FocusMode = Control.FocusModeEnum.None, TooltipText = tip };
            cb.Toggled += on => EditPlayers("Change game options", p => set(p, on));
            box.AddChild(cb);
            return cb;
        }
        Opt("Players choose their HQ", P.ChooseHQ, (p, on) => p.ChooseHQ = on, "The skirmish setup's head quarters choice (else a free start location at random)");
        if (cfg.IsMod)
        {
            box.AddChild(PwTheme.Para("MIRAGE's game options:", 12));
            Opt("Sixth epoch", P.EpochSix, (p, on) => p.EpochSix = on, "MaxEpoch 6: the final stage can be researched");
            Opt("No titans", P.RemoveTitans, (p, on) => p.RemoveTitans = on, "RemoveTitans");
            Opt("Flying units", P.FlyingEnabled, (p, on) => p.FlyingEnabled = on, "FlyingEnabled");
            Opt("Portals", P.Portals, (p, on) => p.Portals = on, "Portals (only without the warp gate)");
            Opt("Building limits", P.BuildingLimit, (p, on) => p.BuildingLimit = on, "BldgMaxCapacity");
        }
    }

    // ================================================================================ Test map's players (Properties)

    /// <summary>who plays Test map: the map's players (PlayersSetup)</summary>
    void BuildTestMapInfo()
    {
        if (EditorSession.Doc == null) return;
        var s = EditorSession.TestSetup("editor:preview", EditorSession.Doc.Name);
        var lines = Skirmish.ActiveSlots(s).Select(x => $"{(x.Slot.Type == "human" ? "You" : "Computer")}: player {x.Slot.Hq + 1}, {(x.Slot.Tribe == "Random" ? "a random tribe" : TribeInfo.Name(x.Slot.Tribe))}, team {x.Slot.Team}");
        _props.AddChild(PwTheme.Para(string.Join("\n", lines) + "\n(the map's players: the Players group)", 12));
    }
}
