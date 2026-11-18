// Model viewer: a small tool to look at the converted models as the game draws them - the list of the manifest's
// models (those whose file is there), an orbit camera, the part states (construction level, damage, epoch, night;
// saddle, gear and wounds of animals), the clips with loop and speed, the party colour and the attached parts of the
// classes that use the model (gamedata.json composites: riders, turrets, build-ups, flags).
//
// Scene: res://scenes/ModelViewer.tscn. Mouse: left drag turns, right / middle drag moves, wheel zooms; F12 saves a
// screenshot to the user folder. Command line (after "--"): --data=<converted data folder> --model=<name>
// --clip=<clip> --party=<colour id> --level=<0..4> --damage=<0..2> --shot=<png path> (renders, saves and quits: docs, tests),
// --yaw=<deg> --pitch=<deg> --dist=<m> --look-y=<m> (the camera; the defaults frame the whole model).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.View.Compose;
using PwRemake.View.Materials;
using PwRemake.View.Models;
using PwRemake.View.Parts;

namespace PwRemake.View.Tools;

public partial class ModelViewer : Node3D
{
    ModelLibrary _lib = null!;
    ModelInstance? _cur;
    Composite? _comp;
    Camera3D _cam = null!;
    Vector3 _target;
    float _yaw = 0.6f, _pitch = -0.35f, _dist = 20;
    readonly Dictionary<string, string> _args = new();
    JsonObject? _composites, _classGfx;

    // UI
    LineEdit _search = null!;
    ItemList _models = null!, _clips = null!;
    Label _info = null!;
    OptionButton _party = null!;
    HSlider _level = null!, _damage = null!, _epoch = null!, _hp = null!, _speed = null!;
    CheckBox _night = null!, _owned = null!, _loop = null!, _parts = null!, _treeSprites = null!, _activated = null!;
    readonly Dictionary<int, CheckBox> _gear = new();
    List<string> _all = new();
    bool _paused;

    public override void _Ready()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--") && a.Contains('=')) _args[a[2..a.IndexOf('=')]] = a[(a.IndexOf('=') + 1)..];
        var cfg = ViewerStage.TestConfig();
        _lib = new ModelLibrary(new AssetSource(cfg));
        var gd = PwRemake.Sim.Core.Json.TryParseFile(Path.Combine(cfg.Data, "gamedata.json"));
        _composites = gd?["composites"] as JsonObject;
        _classGfx = gd?["classgfx"] as JsonObject;
        _lib.PartsOnLinks = PartsOnLinks;
        ViewerStage.Setup(this);
        _cam = new Camera3D { Fov = 40, Far = 3000 };
        AddChild(_cam);
        BuildUi();
        // the models whose file is there
        var dir = _lib.Source.Resolve("assets/manifest.json") is string mf ? Path.Combine(Path.GetDirectoryName(mf)!, "models") : null;
        var have = dir != null && Directory.Exists(dir) ? new HashSet<string>(Directory.GetFiles(dir, "*.glb").Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())) : new HashSet<string>();
        _all = _lib.Manifest.Models.Keys.Where(k => have.Contains(k)).OrderBy(k => k).ToList();
        Filter("");
        if (_args.TryGetValue("model", out var m)) Show(m); else if (_all.Count > 0) Show(_all[0]);
        if (_args.TryGetValue("shot", out var shot)) CallDeferred(MethodName.AutoShot, shot);
    }

    // the parts the classes using a model hang on its links (as the game: GameSession.PartsOnLinksOf), from gamedata.json
    IEnumerable<(string Link, string Gfx)> PartsOnLinks(string model)
    {
        if (_composites == null || _classGfx == null) yield break;
        foreach (var (cls, g) in _classGfx)
        {
            if (!string.Equals(g.Str(), model, StringComparison.OrdinalIgnoreCase) || _composites[cls] is not JsonArray specs) continue;
            foreach (var s in specs)
                if (s is JsonObject o && o["pi"].Int() < 0 && o["kind"].Str() != "rider" && o["gfx"].Str().Length > 0) yield return (o["link"].Str(), o["gfx"].Str());
        }
    }

    async void AutoShot(string path)
    {
        for (int i = 0; i < 20; i++) { Step(1 / 30.0); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        GetViewport().GetTexture().GetImage().SavePng(path);
        GetTree().Quit();
    }

    // ------------------------------------------------------------------------------------------------ UI

    void BuildUi()
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        var panel = new PanelContainer { Position = new Vector2(8, 8), CustomMinimumSize = new Vector2(300, 0) };
        layer.AddChild(panel);
        var box = new VBoxContainer();
        panel.AddChild(box);
        _search = new LineEdit { PlaceholderText = "search models" };
        _search.TextChanged += Filter;
        box.AddChild(_search);
        _models = new ItemList { CustomMinimumSize = new Vector2(290, 260) };
        _models.ItemSelected += i => Show(_models.GetItemText((int)i));
        box.AddChild(_models);
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(290, 0) };
        box.AddChild(_info);
        _party = new OptionButton();
        _party.AddItem("no party colour");
        foreach (var c in PartyColors.All) _party.AddItem(c.Name);
        _party.Selected = 2;
        _party.ItemSelected += _ => ApplyLook();
        box.AddChild(Row("Party", _party));
        _parts = Check(box, "attached parts (composites)", true, () => Show(_cur?.Template.Name));
        _treeSprites = Check(box, "tree foliage sprites", true, () => Show(_cur?.Template.Name));

        var side = new PanelContainer { CustomMinimumSize = new Vector2(280, 0) };
        side.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        side.Position = new Vector2(-288, 8);
        layer.AddChild(side);
        var sb = new VBoxContainer();
        side.AddChild(sb);
        sb.AddChild(new Label { Text = "Building / prop" });
        _level = Slider(sb, "Construction", 0, 4, 4);
        _damage = Slider(sb, "Damage", 0, 2, 0);
        _epoch = Slider(sb, "Epoch", 1, 5, 1);
        _night = Check(sb, "night", false, () => { ViewerStage.SetNight(this, _night.ButtonPressed); ApplyLook(); });
        sb.AddChild(new Label { Text = "Animal / vehicle / character" });
        _owned = Check(sb, "owned (saddle, party parts)", true, ApplyLook);
        foreach (var (bit, name) in new[] { (PartBits.HELMET, "helmet"), (PartBits.ARMOR, "armour"), (PartBits.ARMORSADDLE, "armoured saddle"), (PartBits.STANDARTE, "standard") })
            _gear[bit] = Check(sb, name, false, ApplyLook);
        _hp = Slider(sb, "Hit points %", 0, 100, 100);
        _activated = Check(sb, "activated (medic kit)", false, ApplyLook);
        sb.AddChild(new Label { Text = "Animation" });
        _clips = new ItemList { CustomMinimumSize = new Vector2(270, 220) };
        _clips.ItemSelected += i => PlayClip(_clips.GetItemText((int)i));
        sb.AddChild(_clips);
        _loop = Check(sb, "loop", true, () => { if (_clips.GetSelectedItems().Length > 0) PlayClip(_clips.GetItemText(_clips.GetSelectedItems()[0])); });
        _speed = Slider(sb, "Speed %", 0, 200, 100);
        _speed.ValueChanged += v => _cur?.Anim?.SetTimeScale(v / 100);
        var pause = new Button { Text = "pause / go on" };
        pause.Pressed += () => _paused = !_paused;
        sb.AddChild(pause);
    }

    static Control Row(string label, Control c)
    {
        var h = new HBoxContainer();
        h.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(100, 0) });
        c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        h.AddChild(c);
        return h;
    }

    HSlider Slider(VBoxContainer box, string label, double min, double max, double val)
    {
        var s = new HSlider { MinValue = min, MaxValue = max, Step = 1, Value = val, CustomMinimumSize = new Vector2(150, 0) };
        s.ValueChanged += _ => ApplyLook();
        box.AddChild(Row(label, s));
        return s;
    }

    static CheckBox Check(VBoxContainer box, string label, bool on, Action changed)
    {
        var c = new CheckBox { Text = label, ButtonPressed = on };
        c.Toggled += _ => changed();
        box.AddChild(c);
        return c;
    }

    void Filter(string q)
    {
        _models.Clear();
        foreach (var n in _all) if (q == "" || n.Contains(q, StringComparison.OrdinalIgnoreCase)) _models.AddItem(n);
    }

    // ------------------------------------------------------------------------------------------------ model

    void Show(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _comp?.Dispose(); _comp = null;
        if (_cur != null) { RemoveChild(_cur); _cur.QueueFree(); _cur = null; }
        var tpl = _lib.Template(name, new ModelLoadOptions { TreeSprites = _treeSprites.ButtonPressed });
        if (tpl == null) { _info.Text = $"{name}: cannot load (see the log)"; return; }
        _cur = _lib.Instantiate(tpl);
        AddChild(_cur);
        if (_args.TryGetValue("level", out var lv) && int.TryParse(lv, out var l)) _level.SetValueNoSignal(l);
        if (_args.TryGetValue("damage", out var dg) && int.TryParse(dg, out var dgi)) _damage.SetValueNoSignal(dgi);
        if (_args.TryGetValue("party", out var pc)) { var c = PartyColors.ById(pc); _party.Selected = c == null ? 0 : Array.IndexOf(PartyColors.All, c) + 1; }
        // the attached parts of the classes drawn with this model (the first class's table, parts shown always / when ready)
        if (_parts.ButtonPressed) AttachParts(name);
        _clips.Clear();
        if (_cur.Anim != null) foreach (var c in tpl.ClipNames.Keys.OrderBy(k => k)) _clips.AddItem(c);
        var clip = _args.GetValueOrDefault("clip") ?? _cur.Anim?.Pick("standanim", "idle_0", "stand", "idle", "walk_1");
        if (clip != null) PlayClip(clip);
        ApplyLook();
        var b = tpl.Bounds;
        _target = b.GetCenter();
        _dist = Mathf.Max(6, b.Size.Length() * 1.4f);
        // (a close look: --yaw=<deg> --pitch=<deg> --dist=<m> --look-y=<height of the target>)
        float F(string k, float d) => _args.TryGetValue(k, out var v) && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : d;
        _yaw = Mathf.DegToRad(F("yaw", Mathf.RadToDeg(_yaw))); _pitch = Mathf.DegToRad(F("pitch", Mathf.RadToDeg(_pitch)));
        _dist = F("dist", _dist); _target.Y = F("look-y", _target.Y);
        _info.Text = $"{tpl.Name}  [{tpl.Fourcc}]  {tpl.Info.Arch}\n{tpl.MeshCount} meshes, {tpl.SurfaceCount} surfaces, {tpl.ClipNames.Count} clips, {tpl.Links.Count} links\n" +
                     $"size {b.Size.X:0.0} x {b.Size.Y:0.0} x {b.Size.Z:0.0}, loaded in {tpl.LoadMs:0} ms" + (tpl.Info.Anims != null ? $"\nclips of {tpl.Info.Anims}" : "") +
                     (_comp != null && _comp.Parts.Count > 0 ? "\nparts: " + string.Join(", ", _comp.Parts.Select(p => p.Spec.Gfx)) : "");
    }

    void AttachParts(string gfx)
    {
        if (_composites == null || _classGfx == null || _cur == null) return;
        foreach (var (cls, g) in _classGfx)
        {
            if (!string.Equals(g.Str(), gfx, StringComparison.OrdinalIgnoreCase) || _composites[cls] is not JsonArray specs) continue;
            var act = new List<PartSpec>();
            var shown = new HashSet<int>();
            foreach (var s in specs)
            {
                if (s is not JsonObject o) continue;
                var cond = o["cond"].Str();
                if (cond != "always" && cond != "ready") continue;
                int pi = o["pi"].Int(-1);
                if (pi >= 0 && !shown.Contains(pi)) continue;
                var variants = o["variants"] as JsonArray;
                string part = variants != null && variants.Count > 0 ? variants[0].Str() : o["gfx"].Str();
                act.Add(new PartSpec { I = o["i"].Int(), Kind = o["kind"].Str(), Gfx = part.ToLowerInvariant(), Link = o["link"].Str(), Pi = pi, Anim = o["anim"]?.ToString(), AttackAnim = o["attack_anim"]?.ToString(), Flex = o["flex"].Bool(), WeaponClass = o["weapon_class"]?.ToString(), Offset = o["offset"] is JsonArray off && off.Count == 3 ? Composite.OffsetOf(off) : Vector3.Zero });
                shown.Add(o["i"].Int());
            }
            _comp = new Composite(_cur);
            _comp.Build(act, cls, false, this);
            return;
        }
    }

    void PlayClip(string clip) => _cur?.Play(clip, _loop.ButtonPressed, _speed.Value / 100, null, 0.1);

    void ApplyLook()
    {
        if (_cur == null) return;
        Color? party = _party.Selected <= 0 ? null : PartyColors.All[_party.Selected - 1].Light;
        _cur.SetPartyColor(party);
        var t = _cur.Template;
        if (t.Fourcc == "Anim" || t.Fourcc == "Vehi")
        {
            var gear = _gear.Where(kv => kv.Value.ButtonPressed).Select(kv => kv.Key).ToArray();
            _cur.SetAnimalLook(new AnimalLook { Owned = _owned.ButtonPressed, Gear = gear, Hp = _hp.Value / 100 });
            _cur.SetAnimalTint(_owned.ButtonPressed ? party : null);
        }
        else if (t.Fourcc == "Char") _cur.SetHideMask(_activated.ButtonPressed ? 0u : 1u << PartBits.CHTR_ACTIVATED);
        else _cur.SetBuildingState((int)_level.Value, (int)_damage.Value, (int)_epoch.Value, _night.ButtonPressed);
    }

    // ------------------------------------------------------------------------------------------------ camera, time

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            if ((mm.ButtonMask & MouseButtonMask.Left) != 0) { _yaw -= mm.Relative.X * 0.008f; _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.006f, -1.5f, 0.3f); }
            else if ((mm.ButtonMask & (MouseButtonMask.Right | MouseButtonMask.Middle)) != 0)
            {
                var b = _cam.GlobalTransform.Basis;
                _target += (-b.X * mm.Relative.X + b.Y * mm.Relative.Y) * _dist * 0.0015f;
            }
        }
        else if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp) _dist = Mathf.Max(1, _dist * 0.9f);
            else if (mb.ButtonIndex == MouseButton.WheelDown) _dist *= 1.1f;
        }
        else if (e is InputEventKey k && k.Pressed && k.Keycode == Key.F12)
        {
            var path = ProjectSettings.GlobalizePath($"user://model_{_cur?.Template.Name ?? "none"}_{DateTime.Now:HHmmss}.png");
            GetViewport().GetTexture().GetImage().SavePng(path);
            _info.Text += "\nsaved " + path;
        }
    }

    public override void _Process(double delta) => Step(delta);

    void Step(double dt)
    {
        var dir = new Vector3(Mathf.Sin(_yaw) * Mathf.Cos(_pitch), -Mathf.Sin(_pitch), Mathf.Cos(_yaw) * Mathf.Cos(_pitch));
        _cam.Position = _target + dir * _dist;
        _cam.LookAt(_target);
        if (_cur == null || _paused) return;
        _cur.Advance(dt);
        _comp?.Update(dt, false);
    }
}
