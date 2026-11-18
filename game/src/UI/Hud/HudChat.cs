// The in-game chat (the original's CIngameChatBar, ChatWindow.usl:16-349 / ChatWindow.srf "IngameChatWindow";
// remake docs/spec/ui_groups7_9.md §3): a bar of 550 x 50 px at a height of 1 - 1/3.5 of the screen with the
// target (To All / To Allies / To Enemies), the line (150 characters) and Send. Enter opens it to all, Ctrl+Enter
// to the allies, Shift+Enter to the enemies; the top bar's Chat button opens it, its right click the history of the
// message lines (HistoryWindow.usl: 765 x 515, auto scroll).
// A line to all that starts with "AI<n>:" is a request to the computer player of slot n (help / food / wood / stone /
// gift: src/Sim/Ai/Chat.cs; the original's CW:147-153). MIRAGE 2.6.8: the list also holds every computer player by
// name (a line to it is its request) and the computer players read the lines to the allies / the enemies too
// (Logic/ChatRouting.cs). A request goes as a command (AiRequestCommand), so a replay has it. In a networked game (docs/MULTIPLAYER.md) the lines go to the
// other players through the host (IHudHost.Chat) and come back as message lines to those they are for (all / the
// allies / the enemies of the writer, as each machine's world sees them). The original's chat has no cheat codes.
// Ported from remake/src/ui/chat.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.Sim.Commands;
using PwRemake.UI.Hud.Logic;

namespace PwRemake.UI.Hud;

public partial class HudChat : Control
{
    public const int MaxHistory = 300, BarW = 550, BarH = 50, MaxChars = 150;
    readonly Hud _hud;
    /// <summary>every message line of the game (hud.message): game time, text, kind</summary>
    public readonly List<(int T, string Text, string Kind)> History = new();
    OptionButton _to = null!;
    LineEdit _line = null!;
    Control _bar = null!;
    /// <summary>the list's targets: "all", "allies", "enemies", then "p&lt;id&gt;" for each computer player (ChatRouting)</summary>
    readonly List<string> _targets = new() { "all", "allies", "enemies" };

    public HudChat(Hud hud) { _hud = hud; Name = "Chat"; MouseFilter = MouseFilterEnum.Ignore; ProcessMode = ProcessModeEnum.Always; }

    string Ui(string k, string d) => _hud.Data.Ui(k, d);

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Theme = PwTheme.Theme;
        _bar = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        ((PanelContainer)_bar).AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        _to = new OptionButton { CustomMinimumSize = new Vector2(110, 0), FocusMode = FocusModeEnum.None, TooltipText = Ui("_UI_ChatWind_List_Filter_TT", "") };
        foreach (var k in new[] { "All", "Allies", "Enemies" }) _to.AddItem(Ui("_UI_ChatWind_Option_To" + k, "To " + k));
        _line = new LineEdit { MaxLength = MaxChars, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = Ui("_UI_ChatWind_Edit_Text_TT", "") };
        _line.TextSubmitted += _ => Send();
        var send = PwTheme.Btn(Ui("_UI_ChatWind_Button_Send", "Send"), Send, 80);
        send.TooltipText = Ui("_UI_ChatWind_Button_Send_TT", "");
        row.AddChild(_to); row.AddChild(_line); row.AddChild(send);
        _bar.AddChild(row);
        AddChild(_bar);
        Resized += Place;
        Place();
    }

    void Place()
    {
        var vp = Size;
        _bar.Size = new Vector2(BarW, BarH);
        _bar.Position = new Vector2((vp.X - BarW) / 2, vp.Y - vp.Y / 3.5f);
    }

    public bool IsOpen => _bar != null && _bar.Visible;

    /// <summary>a message line for the history (the HUD's message lines)</summary>
    public void Log(string text, string kind)
    {
        History.Add(((int)Math.Floor(_hud.World.Time), text, kind ?? ""));
        if (History.Count > MaxHistory) History.RemoveRange(0, History.Count - MaxHistory);
    }

    /// <summary>to: "all" | "allies" | "enemies"</summary>
    public void Open(string to = "all")
    {
        // the computer players by name (MIRAGE: every other player of the level; a line to one is its request)
        while (_to.ItemCount > 3) _to.RemoveItem(_to.ItemCount - 1);
        _targets.RemoveRange(3, _targets.Count - 3);
        var me = _hud.Host.Me;
        if (me != null)
            foreach (var p in ChatRouting.DirectTargets(_hud.World, me)) { _to.AddItem(Ui("_MIRAGE_UI_ChatWind_Option_To", "To ") + p.Name); _targets.Add(ChatRouting.To(p)); }
        _to.Selected = Math.Max(0, _targets.IndexOf(to));
        _line.Text = "";
        _bar.Visible = true;
        _line.GrabFocus();
    }

    public void Close() { _bar.Visible = false; _line.ReleaseFocus(); }

    /// <summary>a line typed into the open bar and sent (tests)</summary>
    public void SendLine(string text) { _line.Text = text; Send(); }

    void Send()
    {
        var text = _line.Text.Trim();
        var to = _targets[Math.Clamp(_to.Selected, 0, _targets.Count - 1)];
        Close();
        if (text == "") return;
        var me = _hud.Host.Me;
        // the computer players that read it (ChatRouting): their requests go as commands
        void Requests() { if (me != null) foreach (var (slot, word) in ChatRouting.AiReaders(_hud.World, me, to, text)) _hud.Host.Submit(new AiRequestCommand { Slot = slot, Word = word }); }
        bool direct = to.StartsWith('p');
        // a networked game: to the other players through the host (the line comes back to everybody, this machine too)
        if (!direct && _hud.Host.Chat(to, text)) { Requests(); return; }
        if (direct)
        {
            // to one computer player ("You told <name>: ..."): only it reads the line
            var name = _to.GetItemText(_to.Selected);
            _hud.Message(Ui("_MIRAGE_UI_ChatWind_Msg_Youtold_A", "You told ") + name.Replace(Ui("_MIRAGE_UI_ChatWind_Option_To", "To "), "") + Ui("_MIRAGE_UI_ChatWind_Msg_Youtold_B", ": ") + text, "chat");
        }
        else if (to != "all")
            // to the players I consider friends / enemies (CW:271-322); the computer players among them read it (MIRAGE 2.6.8)
            _hud.Message(Ui(to == "allies" ? "_UI_ChatWind_Msg_ToAllies" : "_UI_ChatWind_Msg_ToEnemies", "") + text, "chat");
        else _hud.Message($"{me.Name}: {text}", Hud.ChatKind(me));
        Requests();
    }

    public override void _Input(InputEvent e)
    {
        if (!IsOpen) return;
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { GetViewport()?.SetInputAsHandled(); Close(); }
    }

    /// <summary>the history window (HistoryWindow.usl: 765 x 515; the game runs on)</summary>
    public void ShowHistory()
    {
        var layer = new CanvasLayer { Layer = 25, Name = "ChatHistory", ProcessMode = ProcessModeEnum.Always };
        var root = new Control { Theme = PwTheme.Theme, MouseFilter = MouseFilterEnum.Stop };
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layer.AddChild(root);
        root.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.45f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var c = new CenterContainer();
        c.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddChild(c);
        var w = new PwWindow(Ui("_UI_ChatWind_Title_Chat", "Chat"), 765, 766) { CustomMinimumSize = new Vector2(765, 515) };
        c.AddChild(w);
        var list = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true };
        list.AddThemeColorOverride("default_color", HudSprites.Hex("#e8d6b0"));
        list.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color(12 / 255f, 10 / 255f, 7 / 255f, 0.6f), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6 });
        static string Esc(string s) => s.Replace("[", "[lb]");
        var dim = PwTheme.Dim.ToHtml(false);
        foreach (var (t, text, kind) in History)
        {
            var col = kind == "bad" ? "ffb090" : kind.StartsWith("chat") ? "fff2d0" : "e8d6b0";
            int colon = text.IndexOf(": ", StringComparison.Ordinal);
            // (a chat line: the sender's name in the player's colour)
            var line = Hud.ChatNameColor(kind) is { } nc && colon > 0 ? $"[color=#{nc.ToHtml(false)}]{Esc(text[..(colon + 1)])}[/color] [color=#{col}]{Esc(text[(colon + 2)..])}[/color]" : $"[color=#{col}]{Esc(text)}[/color]";
            list.AppendText($"[color=#{dim}]{t / 60}:{t % 60:00}[/color] {line}\n");
        }
        if (History.Count == 0) list.AppendText($"[color=#{dim}]-[/color]");
        w.Body.AddChild(list);
        var auto = new CheckBox { Text = "Auto scroll", ButtonPressed = true, FocusMode = FocusModeEnum.None };
        auto.Toggled += on => list.ScrollFollowing = on;
        var row = new HBoxContainer();
        row.AddChild(auto);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        row.AddChild(PwTheme.Btn(Ui("_PlayersWindow_Button_Close", "Close"), () => layer.QueueFree(), 100));
        w.Body.AddChild(row);
        AddChild(layer);
    }
}
