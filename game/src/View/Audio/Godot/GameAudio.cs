// The game's sound in the scene tree: one node (made on first use, below the root, kept across scenes) that owns the
// sound logic (AudioCore with its Music and Atmo, SoundFeedback) and the Godot output (GodotAudioOut). It reads the
// tables of the chosen configuration (GameApp.Assets: assets/sounds.json) and the volumes of the settings, and runs
// once a frame what the JS ran in main.js's 'audio' step and on requestAnimationFrame.
//
// How the game calls it (docs/AUDIO.md):
//   menus          GameAudio.I.Menu()                         the main theme (MusicMgr's MainMenu switch)
//   a game starts  GameAudio.I.StartGame(levelName, atmos, tribe)  the level's playlist and ambience
//   every frame    GameAudio.I.UpdateWorld(dt, gameTime, cameraTarget, yaw, homes, Atmo.State(mat, high, day), cameraHeight)
//   events         Sfx.PlayEvent / Sfx.Ui / Sfx.Voice / Sfx.Feedback.* / Sfx.Anim / Sfx.Music.* (Sfx.cs)
//   the game ends  GameAudio.I.EndGame() (combat music off behind the end screen)
//   game left      GameAudio.Current?.Menu() from GameSession._ExitTree (every exit: menu, load, editor, retry)
//   settings       GameAudio.I.ApplySettings(GameApp.Settings)
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

public sealed partial class GameAudio : Node
{
    static readonly Logger L = Log.For("View.Audio");
    static GameAudio? _i;

    /// <summary>The game's sound (made and added below the scene tree's root on first use).</summary>
    public static GameAudio I
    {
        get
        {
            if (_i != null && IsInstanceValid(_i)) return _i;
            _i = new GameAudio();
            // (deferred: the root may be busy setting up its children; sounds asked for before the node is in the tree
            // are not played - as the JS's before its AudioContext ran)
            if (Engine.GetMainLoop() is SceneTree tree) tree.Root.CallDeferred(Node.MethodName.AddChild, _i);
            return _i;
        }
    }

    /// <summary>The game's sound when it exists (no node is made).</summary>
    public static GameAudio? Current => _i != null && IsInstanceValid(_i) ? _i : null;

    public readonly GodotAudioOut Out;
    public readonly AudioCore Core;
    public readonly SoundFeedback Feedback;
    AssetSource? _assets;
    /// <summary>Follow the app's configuration (GameApp.Assets) when it changes; off for a node made with its own assets.</summary>
    public bool FollowApp;
    bool _worldThisFrame;
    double _gameTime;

    public GameAudio() : this(GameApp.Assets) { FollowApp = true; }

    public GameAudio(AssetSource? assets)
    {
        Name = "GameAudio";
        ProcessMode = ProcessModeEnum.Always;
        Out = new GodotAudioOut(assets);
        Core = new AudioCore(Out);
        // PW_SOUND_LIMITS=js: the browser remake's limits (comparisons: docs/AUDIO.md "How many sounds")
        if (System.Environment.GetEnvironmentVariable("PW_SOUND_LIMITS") == "js") { Core.Limits = SoundLimits.Js; L.Info("sound limits: the browser remake's (PW_SOUND_LIMITS=js)"); }
        Feedback = new SoundFeedback(Core);
        AddChild(Out);
        UseAssets(assets);
        ApplySettings(GameApp.Settings);
        if (_i == null || !IsInstanceValid(_i)) _i = this;
    }

    /// <summary>The configuration's sound tables and files (when the title screen picks another configuration).</summary>
    public void UseAssets(AssetSource? assets)
    {
        if (assets == _assets && assets != null) return;
        _assets = assets;
        Out.StopAll();
        Out.SetAssets(assets);
        var path = assets?.Resolve("assets/sounds.json");
        if (path == null) { if (assets != null) L.Warn($"no assets/sounds.json in configuration {assets.Config.Id}: no sound"); Core.SetData(new SoundDb()); }
        else Core.SetData(SoundDb.Load(path));
        // the crowd sounds' table: the installation's Audio/soundeffects.txt (the client reads Data/Base/Audio's;
        // an overrides/assets/soundeffects.txt of the configuration replaces it)
        Core.Crowd.Clear();
        Core.Crowd.Table = CrowdTable.Load(assets?.Resolve("assets/soundeffects.txt") ?? assets?.Config.GameFile("Audio/soundeffects.txt"));
    }

    /// <summary>The volumes of the settings: master, music, effects, speech, ambience, the interface's sounds, and
    /// sound on / off (main.js applySettings: audio.muted, audio.vol).</summary>
    public void ApplySettings(AppSettings s)
    {
        var v = Core.Vol;
        v.Master = s.VolMaster; v.Music = s.VolMusic; v.Sfx = s.VolEffects; v.Voice = s.VolSpeech; v.Unit = s.VolUnits; v.Atmo = s.VolAmbience; v.Ui = s.VolUi;
        Core.Muted = !s.Sound;
        Core.ApplyVolumes();
    }

    public override void _Ready()
    {
        Core.ApplyVolumes();
        L.Info($"sound: driver {AudioServer.GetDriverName()}, {AudioServer.GetMixRate()} Hz, buses {AudioServer.BusCount}");
    }

    /// <summary>No music until the menus or a game ask for it (Menu / StartGame): the MainMenu switch is on from the
    /// start, so without this the main theme would play under the start films (IntroScreen).</summary>
    public bool MusicHeld = true;

    // ---------------------------------------------------------------- what the game tells it
    /// <summary>The menus: the main theme (MainMenu switch; the game's switches off), no ambience. Also when a game
    /// is left (GameSession._ExitTree) - MusicMgr.usl turns the game's switches off whenever no game runs.</summary>
    public void Menu()
    {
        MusicHeld = false;
        Core.Atmo.Start(null);
        Core.Mus.StopGame();
    }

    /// <summary>A game starts: the level's playlist (LevelName, else its atmosphere's, else default) and ambience
    /// (material_atmos "jungle.txt"; "No Atmos": none). tribe: the player's (the Hometown theme).</summary>
    public void StartGame(string? levelName, string? atmos, string? tribe)
    {
        MusicHeld = false;
        Core.Mus.StartGame(levelName, atmos, tribe);
        Core.Atmo.Start(atmos);
    }

    /// <summary>The fights are over (a game or mission ended): no combat music behind the end screen.</summary>
    public void EndGame() => Core.Mus.StopCombat();

    /// <summary>the ambience template while the game runs (a map's biomes: BiomeAmbience) - cross-faded; the same one: nothing</summary>
    public void SetAmbience(string? atmos) => Core.Atmo.Switch(atmos);

    /// <summary>Once a frame of a running game (main.js 'audio' step): the listener (the camera's target and yaw), the
    /// music's timed switches (game time; homes(): the player's home objects, Music.HomeClass), the ambience's state
    /// (Atmo.State(sound material under the view, camera more than 12 m above the ground, day)); cameraHeight: the
    /// camera above the ground - the hearing distances shrink when it is low (SoundLimits.HeightK; NaN: kept).</summary>
    public void UpdateWorld(double dt, double gameTime, V3 listener, double yaw, Func<IEnumerable<V3>>? homes, int atmoState,
        double cameraHeight = double.NaN)
    {
        Core.Listener = listener;
        Core.Yaw = yaw;
        if (!double.IsNaN(cameraHeight)) Core.HearK = SoundLimits.HeightK(cameraHeight);
        _gameTime = gameTime;
        Core.Mus.Update(gameTime, listener, homes);
        Core.Atmo.Update(dt, atmoState, gameTime);
        _worldThisFrame = true;
    }

    public override void _Process(double delta)
    {
        if (FollowApp && GameApp.Assets != null && GameApp.Assets != _assets) UseAssets(GameApp.Assets);
        Core.Frame();
        Core.Mus.StepFades(delta);
        // in the menus (no world): the music's switches still run - the Opening / Trigger end, MainMenu; not before
        // anything asked for music (the start films play their own sound: the main theme starts with the title screen)
        if (!_worldThisFrame && !MusicHeld) Core.Mus.Update(_gameTime);
        _worldThisFrame = false;
    }

    public override void _ExitTree()
    {
        if (_i == this) _i = null;
    }

    /// <summary>Stop everything for good (exit game).</summary>
    public void Shutdown()
    {
        Core.Shutdown();
        Out.StopAll();
        Out.Streams.Clear();
    }
}
