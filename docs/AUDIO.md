# Sound and music

The game's sound is the original's: its sound events, speech, music and ambience, played from the player's own
installation (`Data/<folder>/Audio/Sound`, `Audio/Music`) by the rules of the original client - ported from the browser
remake (`remake/src/engine/audio.js`, `music.js`, `atmo.js` and the sound decisions of `ui/feedback.js`, pw-exporter
98c7703). Folder: `game/src/View/Audio/`.

## Layers

```
game/src/View/Audio/            the logic: what plays, how loud, where (no Godot types; the tests compile these files)
  WavDecoder.cs                 IMA ADPCM (wav 0x11) -> PCM16, bit-exact with the JS decodeWav; plain PCM wavs
  SoundDb.cs                    assets/sounds.json typed: events, material effects, files, music tables, ambience templates
  AudioOut.cs                   IAudioOut / ISoundBuffer / ISoundHandle / IMusicTrack: what the logic needs from an output
  Audio.cs        AudioCore     engine/audio.js: events (sequential / random variants), hearing distances, pan, the
                                original's limits of world sounds (SoundLimits), speech acks one at a time, material
                                hits, animation sound events
  Music.cs        Music         engine/music.js: the original's MusicMgr - switches, playlists, combat, hometown, fades
  Atmo.cs         Atmo          engine/atmo.js: CAtmoSoundMgr - 36 states per template, beds, event playlists
  SoundFeedback.cs              ui/feedback.js (UISndMgr.usl, Game.usl, FeedbackEventMgr.usl): which sound for what
  AnimSounds.cs                 game/anim.js setEvents/update: a model's sound table (manifest), alternatives, firing
  CrowdSounds.cs  CrowdSounds   the client's crowd sounds (soundeffects.txt "Additionals"; not in the browser remake)
game/src/View/Audio/Godot/      the Godot side
  AudioBuses.cs                 buses made in code; pan buses (WebAudio's equal-power panning)
  AudioStreams.cs               files -> AudioStreamWav (decoded / loaded) and AudioStreamMP3, cached
  GodotAudioOut.cs              IAudioOut: pooled AudioStreamPlayers, gain ramps, music tracks
  GameAudio.cs                  the node: owns AudioCore + SoundFeedback + the output, follows the configuration/settings
  Sfx.cs                        the short way to call it (static)
  AudioSelfTest.cs/.tscn        a headless run through a sequence of sounds with checks
```

The logic decides, the output plays: the JS had both in one class around WebAudio; here `AudioCore`, `Music` and
`Atmo` call an `IAudioOut` - `GodotAudioOut` in the game, a recorder in the tests - so the rules are tested without an
engine and compared with the JS run in node.

## Data

`assets/sounds.json` of the configuration's data (made by the toolkit's pipeline/build_sounds.py):

| key | from the original | used by |
|---|---|---|
| `events` | Scripts/Server/init/sounds*.txt: `name: {g: global, seq: sequential, s: [[file, vol 0..100, maxHear]]}` | AudioCore.Play |
| `material` | MaterialEffects.txt: weapon fx -> target fx -> [sfx event, gfx] | AudioCore.Hit |
| `files` | original wav path -> file below `assets/snd/` (animation sounds of the GSF models) | AudioCore.AnimEvent |
| `music` | Audio/MusicMgr.txt (switches, misc) + MusicTracks.txt (playlists, tracks) | Music |
| `atmo` | Audio/AtmoTemplates/*.txt: 36 states each | Atmo |

Animation sounds: `assets/manifest.json` `models[x].sounds = {clip: [[time, wav, vol, maxHear, group, minFade, pitch]]}`
(AnimSounds; data prepared before the toolkit's branch toolkit-pitch, 2026-11-18, has the first five). The crowd
sounds' table: the installation's `Audio/soundeffects.txt` (`AssetSource`: an `overrides/assets/soundeffects.txt` of
the configuration first, else the last Data folder that has `Audio/soundeffects.txt` - the client reads Base's).

Files are looked up through `AssetSource`: `assets/snd/<path>` -> the installation's `Data/<folder>/Audio/Sound/<path>`
(the last Data folder of the configuration that has it), `assets/music/<file>` -> `Audio/Music/<file>`; an
`overrides/` file of the configuration replaces either (docs/CONTENT.md). Most wavs are IMA ADPCM (decoded by
WavDecoder, Godot's own IMA format has another layout), the rest PCM (loaded by Godot's
`AudioStreamWav.LoadFromBuffer`); music is mp3 (`AudioStreamMP3`). Decoded streams are cached per file. A missing file
is logged once and stays silent (the cloud copy of the game lacks some).

## Channels and buses

| JS channel | bus | volume (settings) |
|---|---|---|
| (master) | Master | `volMaster` |
| `music` | Music | `volMusic` |
| `sfx` | Effects | `volEffects` |
| `voice` | Speech | `volSpeech` |
| `atmo` | Ambience | `volAmbience` |
| `ui` | UI | 0.8 (the JS had no setting for it) |

The buses are created in code (`AudioBuses.Ensure`). A sound the JS sent through a `StereoPannerNode` (every world
sound, the ambience's events) plays on a pan bus of its channel (`"Effects pan -0.3"`, made when first needed): the
bus's volume and an `AudioEffectPanner` reproduce WebAudio's equal-power law for a mono input (a centred world sound is
0.707 per side, as in the JS), in steps of 0.1. `AudioStreamPlayer3D` is not used: Godot's distance models and
panning law are not the JS's, and its listener needs a `Camera3D` in the viewport (an `AudioListener3D` alone stays
silent - checked headless).

## The rules (as the JS)

* **World sounds** (`AudioCore.PlayFile` with a position): the original client's limits (next section) - heard
  within the event's hearing distance x the camera height's factor of the listener (the camera's target on the
  ground), at gain `vol/100 x min(1, minFade / d)`, not under 0.01; panned by the angle to the camera's yaw
  (`sin(a) x min(1, d/30)`, at most 0.8); at most 21 at a time, 4 of one wav. Gain and pan are fixed at the start, as
  in the JS.
* **Events**: sequential events play their sounds in turn, the others one at random. Global events are 2D on the UI
  bus, `voice_*` on Speech, the rest world sounds on Effects. `Play(..., world: true)` is the client's Play3DSound of
  a global event: a world sound at the place (the death cries).
* **Speech acks** (`Ack`): one at a time - while one speaks the next is not said; the first existing name of a
  fallback chain (`voice_<class>_<kind>` -> `voice_<kind>`).
* **Hits**: `MaterialEffects[weapon fx][target fx]` (else the weapon's `Unit` row) -> event.
* **Animation sounds**: entries of a clip sharing a time are alternatives (one at random); the wav maps through
  `files`; played at the GSF's pitch of the entry (the client plays the wav at its rate x pitch, no random: C3DSound
  0x7a2f70, set from CSoundEventMgrClient 0x48fdf0's 6th argument, which the callers fill from the sound table - 1 for
  most, 0.5 .. 1.8 for the rest: an idle cry of the T-rex 1.3 / 1.6, a worker's 0.8 ...); data without it: 0.95 ..
  1.05 at random (the browser remake). Default hearing distance 50; only of what is on screen; the slot they take by
  what the entity does (`AnimSounds.Priority`, next section). One started with a sound group counts for the crowd
  sounds (below).
* **Music** (MusicMgr): the switches MainMenu < Loading < Background < BackgroundLeveled < Hometown < Region < Trigger
  < Opening < Combat < Sequence < GameEnd - the highest one that is on plays. The level's playlist by LevelName, else
  its atmosphere (`savanna_txt`), else `default`. Combat after attacks at 3 places within 10 s (the minimap's attack
  events: hits gathered 0.15 s), off after 10 s without one. Hometown: the listener within 75 m of a home object for
  5 s (only the Aje's resource collector exists in the game; `Music.HomeClass`). Trigger / BackgroundLeveled: the
  trigger action MUSI. Sequence: silence during a cutscene. Loading (the original's JOURNAL_MUSIC): silence while a
  journal entry is read on the loading screen (`Music.Journal`). Opening and Trigger play once. Fades: the switch's fade in
  / out (the higher switch's fade in when going up, the lower's fade out going down). The track's own volume is its
  fade x the duck (0.5 while a mission's speech plays); the bus has the music and master volumes.
* **Ambience** (CAtmoSoundMgr): the state = ground material (0..8) + 9 x (camera more than 12 m above the ground) +
  18 x day. The state's bed loops, faded in over 1.5 s; another material switches after 0.5 s, height or daytime at
  once. Its event playlist plays an entry's wav a..b times every c..d s at volume e..f (0: a pause) and pan g..h - 100 %,
  then a random entry. `"No Atmos"`: silence.

## How many sounds (the original's limits)

With many units on screen every idle dinosaur's stand animation cries once a loop (`standanim`, `feeding`, `rest`,
`shake` carry sound events in the GSF; a hundred idle animals ask for ~30 cries a second). The browser remake played
each one it had a slot for, heard 1.6 x as far as the original and only a little quieter at a distance - a wall of
noise, and a fight's hits found the slots taken. The original client limits them in two places (PWClient.exe,
`objdump -d -M intel`; addresses for the next reader):

| where | rule | here |
|---|---|---|
| `CSoundEventMgrClient` 0x48fdf0 (every 3D sound event, animation sounds included) | the hearing distance and the min fade distance x `0.25 + clamp((camera above ground - 10) / 25, 0, 0.75)`; farther than that from the listener: not played | `SoundLimits.HeightK`, `AudioCore.HearK` (GameSession passes the camera's height) |
| the same, `C3DSound` 0x7a3010 | `AIL_set_sample_3D_distances(maxfade, minfade)`: Miles' roll-off - full up to the min fade distance, then `minFade / d` | `MilesFalloff` |
| `C3DSoundMgr` 0x7a0cb0 (every sample of priority < 2: world sounds 0, crowd sounds 1) | refused while more than 20 play; refused while 4 instances of the same wav play, or the newest of them started less than 0.04 s ago | `MaxPlaying` 21, `MaxSameFile` 4, `SameFileGap` 0.04 |
| `C3DSoundMgr` 0x44def0 / 0x7a0880 | the listener: the camera moved towards its target by `min(0.95 x distance, 70 x pitch)` - near the target, a few metres up | the camera's target on the ground (no height) |

`AudioCore.Limits` (`SoundLimits`) holds them; `SoundLimits.Js` is the browser remake's (x 1.6, `(1 - d/R)^1.5`, 25
at a time, nothing else) - `LogicMatchesJs` replays the JS with it, `PW_SOUND_LIMITS=js` runs the game with it (for
comparisons). The min fade distance is in the original's tables (GSF sound table: 5 m for most animation sounds;
sounds*.txt: 6 m for hits, 10 m for explosions and big projectiles, 148 m for the "heard everywhere" events) but not
in the prepared data yet (sounds.json `s: [file, vol, maxHear]`, manifest `[t, wav, vol, maxHear, group]`): a 4th /
6th value is read when the toolkit exports it, else `SoundLimits.DefaultMinFade` gives the tables' values.

The remake adds **slots by priority** so idle chatter never takes the fights' sounds (the original had one priority):
`AnimSounds.Priority` - attacks, being hit, dying, a building's destruction, the `sndgroup_combat` group: *Combat*
(all 21 slots; hits, impacts, explosions, death cries and the other sound events too); a unit standing about (task
idle / hold) or an animal roaming but not walking (feeding, resting, its stand animation) and a building's loops:
*Ambient* (only while fewer than 8 play); walking and work: *Activity* (fewer than 15). The interface, speech, music
and ambience are not world sounds and are never limited.

Every 10 s the log has at Debug (`View.Audio`) `world sounds in 10 s: N of M played; refused: too far, too quiet, no
free slot, same wav` - with `View.Audio` at Trace each start is a `play <file> Sfx gain .. at x,z` line.

Measured:

| case | browser remake's limits | original's limits |
|---|---|---|
| `IdleCrowd` (test): 100 idle achelousaurus, ~33 cries asked a second | 15.1 started a second, 25 at once | 4.9 a second, at most 8 idle at once |
| ... then 30 swordsmen fighting among them: hits played | 96 of 500 | 255 of 500 (as many as without the crowd) |
| xvfb, skirmish multi_2_jun_001, `--spawn` 25 + 25 warriors, 9 + 9 dinosaurs, 80 ridden dinosaurs (game 10-70 s) | 3.4 world sounds a second, mean gain 0.39 | 2.7 a second, mean gain 0.10 (the sum of the gains: -6.7 dB) |

(In the xvfb runs few idle sounds reach the speakers at all: the ridden dinosaurs' idle sets use `ride_idle_*` clips
without sound events - the noisy idle clips are the wild animals' and the titan triceratops' `standanim`, `feeding`,
`rest`, `shake`, and the soldiers' `*_stand_to_fight` / `*_fight_to_stand`.)

## Crowd sounds (soundeffects.txt)

Ported 2026-11-18 from the client (`CrowdSounds.cs`; the browser remake had none). `Audio/soundeffects.txt` "Additionals"
names sound groups with entries (loader `CSoundEventMgrClient` 0x490f70: `wav`, `volume` (90), `randomweight` (1; 0:
not added), `mintriggers` / `maxtriggers` (1), `virtuallength` s (1, kept in whole ms), `virtualvolume` (1),
`minfadedistance` / `maxfadedistance` / `maxhearingdistance` (0); the group's minimum: its smallest `mintriggers`).
Every animation sound of the GSF names a group (`sndgroup_combat` the strikes, `sndgroup_move_fighter` the soldiers'
steps, `sndgroup_move_dinos_*` the dinosaurs' steps ...); each one that is started (it passed the limits above) is a
trigger of its group at its place (0x4900e9 -> 0x48fd90; refused or unheard sounds do not count). Every 0.5 s
(0x490340), for each group of the table:

1. the triggers older than 3 s are dropped; the others are gathered into crowds: points chained within 32 m are one
   crowd (0x7ac6b0 joins crowds whose boxes - nearest points - are closer than 32 m); its place is the middle of its box;
2. a crowd sound that was near no crowd in the last pass is stopped; the others are moved to the nearest crowd within
   32 m; one older than its `virtuallength` is forgotten (it plays on to its end but holds the place no longer);
3. a crowd of at least the group's minimum: 1 - the `virtualvolume` of every held crowd sound within 32 m of it is
   left; among the entries with `mintriggers` <= its triggers <= `maxtriggers` and a `virtualvolume` within that, one
   is drawn by `randomweight` (rand() x sum >> 15, the first whose running sum passes it) and plays at the crowd's
   middle: a world sound under the limits above (the client's priority 1 - the same C3DSoundMgr limits as priority 0;
   here the slots of the fights for the groups named combat / fight, of the walking for the others), with the entry's
   volume, min fade and hearing distance (an entry without a hearing distance is heard nowhere), at the wav's own speed
   (the client passes pitch 0).

The original's table (Base): `sndgroup_combat` 12 .. 100 strikes -> `sndgroup_fight_large.wav` (99 %, min fade 10 m,
heard 76 m); `sndgroup_move_fighter` 10 .. 100 steps -> the march `sndgroup_move_fighter.wav` (48 %, 3 s);
`sndgroup_fight` (war drums) and `sndgroup_move_few_dinos` have entries but no GSF sound names those groups, and the
dinosaurs' step groups have no entries; the second entries of `sndgroup_combat` (fight_med) and `sndgroup_fight` (the
small drum) ask for 8 .. 5 triggers - never (kept). The wavs are in the installation's `Audio/Sound/10_sndgroups/`.
Not taken: the sound following its crowd (the remake's world sounds keep the gain and pan of their start, above).
Debug log (`View.Audio`): `crowd sound <group>: <wav> at x,z`.

## How the game calls it

`GameAudio.I` is made on first use below the scene tree's root and lives across scenes; it follows the configuration
the title screen picks (`GameApp.Assets`).

```csharp
// menus (and after a game)
GameAudio.I.Menu();                                         // the main theme
GameAudio.I.ApplySettings(GameApp.Settings);                // after the options menu
// a game starts (main.js: levelName = the map's LevelName, atmos = its material_atmos "jungle.txt", tribe = the player's)
GameAudio.I.StartGame(levelName, atmos, tribe);
// every frame of the game (main.js 'audio' step)
GameAudio.I.UpdateWorld(dt, world.Time, cameraTarget, cameraYaw,
    () => homesOfThePlayer /* positions of Music.HomeClass[tribe] objects */,
    Atmo.State(soundMaterialUnderView, cameraHeightAboveGround > 12, !world.IsNight), cameraHeightAboveGround);
// events
Sfx.PlayEvent("FX_CBigBuildingExplosion", pos);            // a sound event (PSND trigger action, effects' FX_ events)
Sfx.Ui("ui_click");                                         // interface: the named sound, else the click
Sfx.Voice("aje_allosaurus", "selected");                    // speech, one at a time
Sfx.Feedback.Selected(units); Sfx.Feedback.Ordered("move", units); Sfx.Feedback.Error();
Sfx.Feedback.Placed(tribe); .Built(pos); .Trained(pos); .Upgrade(epoch); .LevelUp(isCharacter, pos);
Sfx.Feedback.Died(name, cls, pos); .Destroyed(ajeOwner, pos); .Impact(...); .Hit(weaponFx, targetFx, pos);
Sfx.Feedback.Attacked(x, z, time);  var place = Sfx.Feedback.FlushAttacks(time);   // minimap marker + combat music
Sfx.Feedback.UnderAttackWarning();                          // own objects; timing: AttackWarnings
Sfx.Feedback.AllyUnderAttack();                             // an ally attacked / a computer ally's call for help
Sfx.Anim(ev, pos, AnimSounds.Priority(clip, ev.Group, task)); // AnimSounds.Fire(...) of a visible entity
Sfx.File(file, 100, null, 0, AudioChannel.Voice, offset);   // mission speech starting late, cutscene sounds
Sfx.Music.Trigger("24_location_temple", loop: false);       // trigger action MUSI
Sfx.Music.Sequence(true / false);                           // a cutscene starts / ends
Sfx.Music.Journal(true / false);                            // the loading screen's journal voice starts / ends
Sfx.Duck(true / false);                                     // music down while a dialogue line plays
GameAudio.I.EndGame();                                      // the fights are over: no combat music behind the end screen
```

The world events reach the interface's feedback (ui/feedback.js, ported in game/src/UI); it decides what is seen
(fog of war, on screen) and calls `Sfx.Feedback` where the JS played a sound. The sound material under the view
(`game/maps/source.js soundMaterial`) belongs to the maps; the caller passes the number.

**Heard anywhere, heard near** (as the original): every world sound - the death cries included, whoever's unit died
(Effects.usl CDeathSound plays `voice_<class>_die` with Play3DSound at the unit although the tables mark the events
global) - is heard only within its hearing distance from the camera (no distance in the tables: 60 m). Heard anywhere
are only the player's own feedback (placed, built, trained, research, storage full, boarded ...) and the warnings:
* own objects attacked: `ui_warn_under_attack` and a ping, once a fight - again only after more than 10 whole seconds
  without attacks (Player.usl IWasAttacked, WARN_UNDER_ATTACK_TIME; `AttackWarnings`; not every 10 s of a fight);
* an ally's objects attacked (co-op seats, computer allies, a mission's allies): the same timing per ally, the white
  `AIHelp` minimap marker, the line "Your ally ... is under attack!" and `ui_message` - the original's signs of an
  ally in trouble: a computer ally's "Help! My Base is attacked ..." (AiGoalGuardVillage.usl:630 SendChatMsg with its
  base; Game.usl:404-412 puts the AIHelp marker there, MiniMap2.usl: white, 1.2 s) is a newsticker line, and every
  newsticker line plays `ui_message` (NewsTicker.usl:91). The original warned only of the computer allies' calls;
  the remake warns of any ally (co-op). The call for help itself (`aiHelp`, Sim/Ai/Chat.cs) gives the marker at the
  ally's base and the sound too.

## Tests

* `dotnet run --project tests/PwRemake.Tests -- Audio` (with `PW_DATA=/opt/data/base`, optional `PW_SOUND` = an
  `Audio/Sound` folder):
  * `AdpcmMatchesJs`: the decoder against `tests/golden/audio_adpcm.json` - the JS decodeWav run in node
    (`tests/golden/audio_golden.mjs`) on two synthetic files (stereo; mono with a partial block) and the game's wavs
    (28 IMA ADPCM + 19 PCM staged): every sample equal (sha256).
  * `LogicMatchesJs`: 170 operations (events, hearing distances, pan, the cap, sequential variants, material hits,
    animation events, speech acks, late starts, the music's switches through a game, the ambience over 15 minutes of
    states) replayed against `tests/golden/audio_logic.json` - the JS audio.js / music.js / atmo.js run in node with
    a recording AudioContext and the same random numbers (mulberry32): the same sounds (file, channel, gain, pan,
    panner, rate, loop, offset), switches, tracks, start positions and ambience states.
  * `MusicFades`, `AtmoBeds`, `FeedbackRules`, `AnimSoundTable`, `SoundDbOnRealData`.
  * `AllyDeathsAndWarnings`: a death cry far away is not heard, near it is a panned world sound; the warnings once a
    fight per attacked player; the ally's sound (`ui_message`) and the own one.
  * `SoundLimitsOriginal`: 4 of one wav, 0.04 s apart, 21 at a time, the slots by priority, the hearing distance and
    1/d loudness, the camera height's factor, `AnimSounds.Priority`.
  * `AudioAnim` (AudioAnimTests): `CrowdTableOfTheOriginal` (the loader's values and defaults), `CrowdSoundsCount`
    (11 strikes nothing, 12 the din at the box's middle, held 4 s, forgotten, stopped when the crowd is gone; 8 .. 5
    never; two crowds; a chain of strikes 20 m apart one crowd; marching at 10), `CrowdAndPitchInAudioCore` (only
    started sounds count; the din's gain and speed; the GSF's pitch as the speed, else 0.95 .. 1.05; the manifest's
    7 values), `PitchInTheManifest` (the prepared data: with or without the pitch).
  * `IdleCrowd`: 100 idle achelousaurus (stand animation, ~33 cries asked a second) for a minute, then 30 swordsmen:
    the browser remake's limits play ~15 cries a second (25 at once), the original's ~5 (at most 8 idle at once); the
    fight's hits play as often among the crowd as without it (the JS's: 96 of 500 against 255).
* `godot --headless --path game res://src/View/Audio/Godot/AudioSelfTest.tscn -- [--install <ParaWorld>] [--data
  <converted data>]`: the dummy audio driver (nothing is heard, the mixer runs) - buses, a stream for every wav found,
  mp3, interface sounds, speech, world sounds near and far, the cap, hits, animation sounds, menu / game / combat /
  cutscene / trigger music, the ambience's bed and fade, the pan measured on the bus peaks (22.1 dB apart at pan
  -0.9, as WebAudio). Exit code 0 when all checks pass.

Regenerate the goldens after a change of the JS: `node tests/golden/audio_golden.mjs <pw-exporter>/remake/src/engine/audio.js
<Audio/Sound>` and `node tests/golden/audio_logic.mjs <pw-exporter>/remake/src/engine <data>/assets/sounds.json`.

## Ported / not ported

Ported: everything of engine/audio.js, music.js and atmo.js, the sound choices of ui/feedback.js and ui/gamefx.js
(`FX_<effect>`), the sound side of game/anim.js's events; from the original client beyond the JS: its limits of world
sounds, the crowd sounds (soundeffects.txt) and the GSF's pitch of animation sounds.

Differences:
* no "click for sound" unlock (`unlock`, `resume`, `ready` waiting for a user gesture): Godot plays at once.
* a world sound started with an offset past the file's end is not counted against the cap (the JS counted it and
  never released it).
* the world sounds follow the original client's limits, not the JS's (section "How many sounds"; `SoundLimits.Js`
  keeps the JS's for the golden test).
* animation sounds at the GSF's pitch when the data has it (the JS: 0.95 .. 1.05 at random); crowd sounds (the JS: none).
* pan in steps of 0.1 (pan buses) instead of continuous.
* the music's fades run on real time (`StepFades`) like the JS's requestAnimationFrame; the switches on game time.
* kept as in the JS (the original data's quirks): material rows naming an event with a trailing blank
  (`sfx_proj_metal_small_hit_wood `) play nothing; the Hometown query finds only the Aje's resource collector.

Not here (other folders call it): the visibility tests of the feedback, the sound material of the map
(game/maps/source.js `soundMaterial`), firing the animation events with the clip times (the animation player calls
`AnimSounds.Fire`), the cutscenes' and dialogues' speech scheduling (ui/cutscene.js, ui/mission.js: they call
`Sfx.File` / `Sfx.Duck` / `Sfx.Music.Sequence`), and an options menu with a separate UI volume.
