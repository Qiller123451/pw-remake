// The start films (the original's client start-up, PWClient.exe 0x456a86..0x456b19): the publisher's logo
// (sf_logo.bik), SEK's (sek_logo.bik), the partners' (partner.bik), then the intro film (intro.bik: the dinosaur
// battle). The client plays the three logos when the config value Global/PlayIntro is >= 0 and the intro when it is
// >= 1; MIRAGE replaces films of Data/Base by its own of the same name (MIRAGE.info: tryfilereplace data/base ->
// data/MIRAGE), which the prepare tool's finder does (tools/prepare/films.py start_films).
//
// Godot plays Ogg Theora only: the prepare tool converts the films into the configuration's overrides
// (assets/video/<name>.ogv); a film that is not there is left out, and without any the title screen comes at once.
// Esc skips the rest, a click or Space the film on show. Options > Gameplay "Play the start films" (settings.json
// introFilms) turns them off; --no-intro skips them once (docs/MENUS.md, docs/DEBUG.md).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.UI;

public partial class IntroScreen : Control
{
    static readonly Logger L = Log.For("UI.Intro");

    /// <summary>the client's start films in the order it plays them (PWClient.exe 0x456abf, 0x456ad5, 0x456aeb, 0x456b09)</summary>
    public static readonly string[] StartFilms = { "sf_logo", "sek_logo", "partner", "intro" };

    /// <summary>the converted start films of a configuration, in order (assets/video/&lt;name&gt;.ogv)</summary>
    public static List<string> Films(AssetSource? assets) =>
        assets == null ? new() : StartFilms.Select(f => assets.Resolve("assets/video/" + f + ".ogv")).Where(p => p != null).Cast<string>().ToList();

    readonly List<string> _files;
    readonly Action _done;
    VideoStreamPlayer _video = null!;
    readonly FilmFit _fit = new();
    int _i = -1;
    double _t;
    bool _over;

    public IntroScreen(List<string> files, Action done)
    {
        _files = files;
        _done = done;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        var bg = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        _video = new VideoStreamPlayer { Expand = true, MouseFilter = MouseFilterEnum.Ignore, Bus = "Master" };
        _video.Finished += Next;
        AddChild(_video);
        Next();
    }

    /// <summary>the next film, or the end</summary>
    void Next()
    {
        if (_over) return;
        _video.Stop();
        while (++_i < _files.Count)
        {
            var stream = new VideoStreamTheora { File = _files[_i] };
            _video.Stream = stream;
            _fit.Reset();
            _video.Play();
            _t = 0;
            if (_video.IsPlaying()) { L.Info($"start film {_i + 1}/{_files.Count}: {_files[_i]}"); return; }
            L.Warn($"start film {_files[_i]} cannot be played");
        }
        Finish();
    }

    void Finish()
    {
        if (_over) return;
        _over = true;
        _video.Stop();
        _done();
    }

    public override void _Process(double delta)
    {
        if (_over) return;
        _t += delta;
        // the film's picture as large as the window allows, its shape kept; the bars painted into the films go off the
        // screen (FilmFit)
        _fit.Apply(_video, Vector2.Zero, GetViewportRect().Size, delta);
        // (a stream that stopped without its Finished signal - a broken file - must not hold the start)
        if (_t > 1 && !_video.IsPlaying()) Next();
    }

    public override void _Input(InputEvent e)
    {
        if (_over) return;
        // the event is marked handled first: Finish (Esc, or Next after the last film) hands over to the title screen,
        // which takes this screen out of the tree - GetViewport() is null after it
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.Keycode == Key.Escape) { GetViewport()?.SetInputAsHandled(); L.Info("start films skipped"); Finish(); }
            else if (k.Keycode is Key.Space or Key.Enter) { GetViewport()?.SetInputAsHandled(); Next(); }
        }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right })
        { GetViewport()?.SetInputAsHandled(); Next(); }
    }
}
