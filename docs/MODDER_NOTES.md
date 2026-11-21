# ParaWorld and MIRAGE: findings for modders

What the ParaWorld remake work (Godot / C# port, its browser predecessor and the export toolkit) found out about how the
**original game** works: engine rules read from `PWServer.exe` / `PWClient.exe`, formulas and quirks of the USL
scripts, the data formats, and bugs in the base game and in MIRAGE. Written for people who mod the original game
(scripts, tech tree, `.txt` configs, GSF models, the exe); nothing here needs the remake.

Conventions:

* **Exe addresses** are virtual addresses, image base 0x400000. "(C)" = `PWClient.exe`, no mark = `PWServer.exe`.
* **Script references**: `FO` = `Scripts/Server/classes/FightingObj/FightingObj.usl`, `CH` = `.../character/character.usl`,
  `B` = `.../buildings/Building.usl`, `A` = `.../animals/Animal.usl`, `CM` = `Scripts/Ai/Modules/AiModuleControlDefault.usl`,
  `CHT` = `Scripts/Ai/misc/AiImprovementMgr.usl` (the "cheat manager"). Base game line numbers unless "MIRAGE" is said.
* **MIRAGE claims** were checked against MIRAGE master **b320f9bb** (2026-11-19, "Merge pull request #490
  seas_carrier_autoattack"); paths below are relative to `GameFiles/Basic/Data/MIRAGE/Scripts/`.
* **(inferred)** = reasoned, not read in code or seen in the game.
* Deeper material: the toolkit repository (github.com/Qiller123451/pw-exporter) `docs/original/*.md` (exe findings,
  GSF details, rendering, campaign data), `docs/GSF_FORMAT.md`, `remake/docs/spec/*.md` (combat, AI, triggers, walls
  engine - all with script line references).

## Contents

1. Executables: general
2. Combat
3. Animations, deaths, hit reactions
4. GSF models
5. Rendering and particles
6. Sound
7. Tech tree and filters
8. Buildings, walls, gates, limits
9. Pathfinding and the map's walk data
10. Computer players
11. Campaign and triggers
12. Map files (.ula)
13. MIRAGE: game options and per-level settings
14. Bugs and quirks - base game
15. Bugs and quirks - MIRAGE (verified on master b320f9bb)

---

## 1. Executables: general

* Two programs: `PWServer.exe` (simulation: `Scripts/Server`, `Scripts/Ai`) and `PWClient.exe` (drawing, sound, UI,
  `Scripts/Game`, cutscenes). The **WallMap code** is byte-identical in both: client address = server address
  − 0x25b640 for 0x7dc000-0x7e3000. The 32 **forest layouts** (32 × 624 bytes) are one table in both (server 0xaf8808).
* **USL binding**: name table 0xae7898 (1124 entries); per class a glue object; the dispatcher numbers the methods from 3
  in **alphabetical order**. AI classes have their own table at 0xae8ea0.
* **Script VM class groups** (bit mask when a VM is created): server game VM `push 0xDB` at 0x4320d4, AI VM 0x4dca1f,
  client 0xD9 at 0x442126 and 0x452a11. Bit 0x20 = the USL binding test classes (`Fred`, `CWrapTest` ...): setting it
  (0xDB → 0xFB) makes them available to scripts.
* **CAiInterface** methods (dispatcher 0x47adc0): AddEditRegionEconomy/Ressources, BlockPyramidSlots, CallModule,
  CheckAIDead, DumpIslMap, GameOver, IsAiPlayer, LockUnit, OnAiStringEvt, PlayerLose, PlayerWin, RemRegionEconomy/
  Ressources, SetAggressionMode (−1 passive, 0 stand ground, 1 defensive, 2 aggressive), SetCustomAttack,
  SetFightModuleAttack, SetModuleBehavior, SetPyramidPopLimit, TreeRemoved, UnlockUnit.
* **Bug: `SetModuleBehavior` (0x402560) writes the wrong player slot** when a player's headquarter is not its slot
  index: a human slot can get type `"ai_X"`, and the server's client check (0x405a3e: type starting `ai_` = not
  human) then drops that client. A patch idea (emulator-tested only): at 0x40260f `call` a routine in the .text padding
  that picks the slot whose `headquater` equals the id.
* **Day clock** `CTimeMgr` (0x7bcab0, (C)0x5745f0): virtual time = real × 144; a day = 10 real minutes; day 6:00-18:00
  (ServerApp.usl offsets 0.25 / 0.75). No `StartTime` in a level = 12:00.
* **Start films** ((C)0x456a86-0x456b19): `sf_logo.bik`, `sek_logo.bik`, `partner.bik` when config
  `Global/PlayIntro` >= 0, `intro.bik` when >= 1. MIRAGE's `MIRAGE.info` `tryfilereplace` lets a same-named file in
  `Data/MIRAGE` replace the base one.

## 2. Combat

**First strike (the engine's, PWServer 0x49A1E0, the attack-walk tick).** Settings `Server/FirstStrike/MinDist` 3,
`MaxDist` 8, `DistFact` 0.9, `MaxHitDist` 9 (exe defaults; no shipped file sets them) - but the tick then sets
MaxHitDist to **0.5** itself, so that setting does nothing. Only against characters, animals, vehicles. P = target
position predicted the clip's hit time ahead, r = approach distance between centres: the strike starts when
MinDist² + r² < |P − A|² <= MaxDist² + r² (2D). Strike point S = P + DistFact × r towards the attacker; the nearest
free spot to S must be within MaxHitDist of S and at least MinDist from the attacker; the unit lunges there with the
clip and gets the `FrstStrk` event. Two more conditions of the tick were not identified.

**Weapon stats** (`UpdateWeapons` FO:7420-7573), per current weapon:
`Dmg = dmg × M(Damage).rel + abs` (projectile weapons first × `ranged_damage`), `Prot`/`RProt` clamped 0..99,
`Duration = 60 / frequency × M(WeaponDuration).rel + abs`, × 0.95 (slot difficulty 8) / 0.9 (9) for computer players
(`GetAICheatModifier`). Modifiers: `/Modifications/<tribe>/<TYPE>/<stat>/<caste>_rel|_abs` - base objects always use
caste `tec` (FO:8395, "boeser HACK"), characters their caste `res|nat|tec`. Quirks: `EndDmg` is not scaled by the
Damage modifier; `MinDmg` is loaded and never used; armour depends on the **current** weapon (switching weapon changes
defence); `settings/_Modifications.txt` is an unused template.

**Damage** (`TakeDmg` FO:4040-4109, `ProvideDmg` FO:4324-4514, `Damage` FO:4007):
```
raw = f × CalcAttackBoni(Dmg) × (1 + AttackBonus[type, class] / 100)          (+ DAMAGE_BLDG bonus on buildings)
melee:      pct = TempDef(max(0, Prot − AP)) + DefenseBonus × M(Defence_<attackerType>)
projectile: pct = TempRangedDef(max(0, RProt − AP))                          (DefenseBonus NOT applied)
dmg = (raw − raw × clamp(pct / 100, 0, 0.99)) × AttackFactor(attacker's player)
hp -= max(ceil(dmg × DefenseFactor(victim's player)), 1)                     (every hit at least 1, integer)
```
* A character inside a transport is immune - except on a damage-cache hit.
* **Damage cache quirk**: a victim caches (attacker, dmg); while the same attacker hits and neither side changes weapon
  or boni, the cached value is reused - the splash factor `f` and new buffs are ignored until invalidated.
* `TakeDirectDmg` / `TakeDirectMeleeDmg` (FO:4554, 4586) skip boni and AttackFactor (and have no `max(0, …)`).
  `Damage()` ignores the invulnerability counter: poison ticks and reflects hit invulnerable units.
* Reflect: Kentrosaurus 20 (A:2816), Dragon Clan skewer wall / gate 5 (B:2151, 2177).
* On-wall ranged defence `MiscValues/<tribe>/Defence_On_Wall` = 50. Item ring: projectiles × 0.5; amulet: all × 0.5.
* **Knock-over** by size-class difference 3..9 (FO:4383): chance 2, 4, 6, 8, 10, 20, 30 %, distance 1, 1.5, 2, 2.5, 3,
  4, 5 m. MIRAGE: switchable (`AllowThrow`, default on).
* **Skulls** go to the player who damaged the unit **last** (`OnKill` FO:3950). MIRAGE `DivideSkulls` (default off)
  splits them among all damage dealers; `BonusSkulls` (default off) raises a unit's value with its kills.
* **Automatic target choice** (`SortEnemyList` FO:4890-4924): a unit whose primary weapon has a minimum range and no
  secondary short / medium weapon skips enemies inside its minimum range and out of its reach.
* **AI fight factors** (`CAiCheatMgr.UpdateFightFactors`, every control think, only difficulty >= 5; identical in
  MIRAGE):
  ```
  mapV = clamp((((W−16)/8) × ((H−16)/8) − 1) / 20 + 0.3, 0.2, 0.6)      W, H = AI map size
  def = clamp(1 − mapV × d × 0.2, 0.5, 2)    atk = clamp(1 + mapV × d × 0.2 × 0.5, 0.5, 2)
  fog on:  Dodo def 0.30 / atk 0.05 of the deviation, Giraffe 0.15 / 0.15, Schnecke 0.03 / 0.30
  fog off: Dodo 0.50 / 0.05, Giraffe 0.75 / 0.75, Schnecke 0.05 / 0.50
  per think at most −0.05 / +0.025 towards the target; difficulty < 5: both 1
  ```

## 3. Animations, deaths, hit reactions

* **Start / loop / end clips**: `AnimAction(clip, n, intro, outro)` plays the start part, n loop parts and the end part
  as one action; loop counts are loop parts (chop_tree's loop part = exactly one 1 s blow, Harvest.usl:643-650).
  `SetSLEAnim(clip, 9999, …)` + `AnimActionStopLoop` = loop until stopped, then the end part.
* **`SetStillAnim(clip, -1)` / `SetAnim(..., -1)`: frame −1 = hold the last frame.** A felled tree's log plays
  `chop_down_0<1..3>` once and holds its last frame - also when a save is loaded (Resource.usl, outside `!p_bLoad`).
* **Character death clip** (CH:2272-2338): killed during a `hit_*` clip → that clip held at its last frame; own
  player's Kill → `harakiri`; on a wall → `hit_rotate_s`; else by the side of the last blow (FO:4380-4434
  `GetDirection`): back `dying`, left / right / none `die_simple`, front `hit_fw`.
* **Beheading** (`DiePerHeadOff` CH:2252-2270, from `CreateCorpse`; MIRAGE CH:3608-3640 the same): killer a character,
  victim not a statue, `Random % 3 == 0`, the killer's swing one of `res_strike_13 / 15 / 1 / 7 / 12`, victim caste
  `res` → `CreateObj(<class>_head)`: a static model in `all_characters.gsf` (163 `*_head` models; `aje_rammer_head` is
  empty).
* **Hit reactions** (`ProvideDmg` FO:4383-4446): a blow of an enemy, ≥ 1 hp, not on a wall, not thrown, not a
  projectile, victim not a building / vehicle / ship, not trapped → `EID_HitReactionOVLAnim_<F|B|L|R>`; the client plays
  the overlay clip of the walk set's slots `hit_reaction_fa / la / ra / ba` (`_fb …` with `Client/HitReaction/UseB`).
  The shipped data has only `hit_reaction` (0.28 s, 55 models).
* **Gait changes**: `Scripts/Server/init/walkanimconfig.txt`; a walk set's GSF slot 28 names its table (`Animals1..4`);
  slots 19-24 = accel_1_2, accel_2_3, accel_3_4, brake_4_3, brake_3_2, brake_2_1.
* **Idle animations**: `Scripts/Game/misc/IdleAnims.txt` (client `CIdleAnimMgr`): only on screen, only animals whose
  task is "Idle", never a wild one (owner −1) in its `standanim`; chance by `Random % total`.
* Sleeping animals `AnimAction("rest", 20 + Random % 40, …)` (Sleep.usl:68-74); fields grow by `SetAnim("grow", 0, step)`
  one step per harvest chunk (B:3436-3445); gates hold frame 50 (open) / 70 (palisade) (B:2465-2535).
* Statues (`hu_berserk_statue_01..04`, `hu_avatar`, `hu_stone_statue`) refuse `BoardTransport` and `EnterBunker`.

## 4. GSF models

* **Header 1 sound entry**: `lstr wav` (below `Audio`), `u32 start frame`, `u32 volume %`, `f32 speed` (= **pitch**:
  the client plays the wav at rate × pitch, no randomness - (C)C3DSound 0x7a2f70; most 1, some 0.5..1.8, e.g. a T-rex
  idle cry 1.3 / 1.6, a worker 0.8), `f32` (always 0), `f32 min fade`, `f32 max fade`, `f32 max hearing distance`,
  `lstr sound group` (crowd sounds, §6). Sounds of one clip sharing a frame are alternatives. (In the remake's
  manifest the pitch is the 7th value of an entry.)
* Header 1 animation entry has a group bit set: 1 idle, 2 walk, 4 fight, 8 work. Clip flags: 0x1 = loop marks
  (int16 first / last frame of the loop part; −1 = none). Header 2 starts at the next 0x10000 boundary.
* **Part attribute bits**: 0-8 LOD mask (bit 0 = full), 9-13 epoch variants I-V, 17 day-only / 20 night lights,
  18 pick volume, 19 shadow stand-in, 14 (without 16) a gate's leaves modelled open (editor only), 21-24 construction
  levels 0-3, 25 finished, 26 / 27 / 28 intact / damage 1 / damage 2; animals: 5 party colour, 6 saddle, 7 helmet,
  8 armour, 9 standard, 10 armoured saddle, 11 misc, 20-27 wounds; characters: 16 activated (medic kit).
  A chunk meant for every state carries 0x3e00000 (169 such chunks in the base buildings). 33 building chunks have
  **no** construction bit - leftover copies from modelling (e.g. the Norse lumberjack cottage's nine `_f0` meshes); the
  game does not show them (inferred from the look).
* **Material flag 0x1000 = party colour**: drawn with D3D `MODULATE2X` (texture × light × colour × 2, (C)0x6ac340); the
  textures under it are painted at about half brightness for that. Flag 0x4 = additive.
* **A link the model lacks**: the client skips it ((C)0x46e5c3: index −1) and the child keeps the position the server
  gives it (the parent's). Example: MIRAGE's `aje_gallimimus_nest` uses `ninigi_dilophosaurus_nest` (no link "Nest") and
  hangs `nest_birds` at "Nest".
* **"Dust trail" table** (header 1 effects): `all_characters.gsf` has 3199 entries, 2300 of them `_dusttrail`. The
  client's effect dispatcher ((C)0x4490d0) **returns at once for `_dusttrail`** - no string `bloodcount`,
  `stonecount`, `force`, `offset_z` exists in the exe: these entries show nothing. Other names = sound event
  `FX_<name>` + the client script class `<name>`. Way to get dust without exe changes (untested): write effect classes
  in `Effects.usl` and rename the entries to them.
* **MaterialEffects.txt**: 21 weapon × 7 target materials, every `GFX` empty; the native path plays only the SFX
  (row = weapon `fx_class`, column = victim `fx_class`, `Water` below 16 m). `CEffectMaterialMgr.GetMaterialEffectGFX`
  exists for client scripts; nothing calls it.
* UVs are bytes / 255 ((C)0x7369e7 …). MIRAGE-written archives (`mirage_animals.gsf`, `seas_products.gsf`) store empty
  chunk-table slots as 0 instead of 0x80000000; `seas_laboratory` has material records beyond the header's count.

## 5. Rendering and particles

* **Particles** ((C)InitPart 0x709740, ProcPart 0x707e30, Process 0x704a10; renderer 0x704d40): Direct3D 9 with **no
  sRGB conversion anywhere**: a sprite = texel × vertex colour in sRGB values; one blend ONE / INVSRCALPHA - additive
  particles get alpha 0 and add their colour to the screen's sRGB value, the others are premultiplied. **The additive
  colour is squared** ((C)0x705329: c × c × party colour >> 16): a colour fade fades with its square; an additive
  particle's alpha is not used at all. Flags: 0x80 bursts, 0x4000 alpha-only fade, 0x80000 particles in world space
  (inherit half the model's speed), 0x20000000 sprites along their flight, 0x40000000 lying on the ground.
* Particle LOD ((C)0x703e40): all slots within 120 m of the camera, fewer to 220 m, fewer still when the last frame drew
  more than half the sprite limit; they fade out between the fog's start and end ((C)0x705191).
* Wall pieces: the engine switches on every variant A-P of the components the piece's BuildName names ((C)0x4f9100).
* **Fog** (sky renderer (C)0x730720-0x730a15): end = 2 × B(a) × k × ViewDepth, start = min(2.5 × A(a) × k × ViewDepth,
  end); k = 1 + 4 × (1 − dot(horizontal view, view)); A, B piecewise linear in the sky alpha a. Row 0 of the sky table
  is the fog colour (alpha forced 255, (C)0x7308f3-0x730a0c).
* **Sun and sky** (CSkyMgr): azimuth 2πf + 3π/2, elevation 0.4 − 0.2 cos(2πf) rad (f = day fraction: 0.6 at noon,
  0.2 at midnight - the sun never sets); colours from `Texture/Scape/<Setting>/skygradient_day.tga` (else
  `skygradient.tga`): columns = the day from midnight, rows 0 sky / fog, 1 ambient, 2 sun. The TGA loader flips
  bottom-up files ((C)0x59ab50).
* **Shadows** (pass (C)0x482765-0x482a42): without `AllObjShadows` only BLDG and DCCO cast (+ ANML / VHCL with
  `BigObjShadows`); trees only with `AllObjShadows = 1` (Shadow_5). Objects use LOD 4 in the shadow pass. With
  `ObjHighDetail = 0` the bit-19 stand-in chunk is drawn. Tree crowns cast nothing in game: their LOD-4 billboards go to
  the depth pass but leave nothing. `bSkipBillcloudShadow` never fires (the flag is only written as 0, (C)0x6e6a38).
  Untested config change for crown shadows: `bin/GfxOptionPropDB.txt` Shadow_5 `ObjHighDetail = '0'`.
* Night: only finished buildings and ships switch lights (every 20 s; parts with bit 20 at night, 17 by day); the scene
  is not darkened at night.
* MIRAGE's **range markers** (`RangeMarkerMgr.usl`): their colours have alpha 0 and the engine's ground rings
  ((C)0x6be370, additive copy of `Effects/Selection.tga`) add them - a glow that keeps the ground's colour.
* **Selection circles** are sized by the model's geometry in the engine; no tech tree value controls them (Building.usl
  comment: "non visible links corrupt selection circle and health bar rendering").

## 6. Sound

**Limits in the client** (`objdump` of PWClient.exe):

| where | rule |
|---|---|
| `CSoundEventMgrClient` 0x48fdf0 (every 3D event) | hearing distance and min fade × `0.25 + clamp((camera height above ground − 10) / 25, 0, 0.75)`; farther: not played |
| `C3DSound` 0x7a3010 | Miles roll-off: full up to min fade, then minFade / d |
| `C3DSoundMgr` 0x7a0cb0 (priority < 2: world 0, crowd 1) | refused while more than 20 play; refused while 4 of the same wav play or the newest started < 0.04 s ago |
| `C3DSoundMgr` 0x44def0 / 0x7a0880 | the listener = camera moved towards its target by min(0.95 × distance, 70 × pitch) |

Min fade in the tables: 5 m for most animation sounds; sounds*.txt 6 m hits, 10 m explosions, 148 m "heard everywhere".
Many idle animals ask for ~30 cries a second; the limits let ~5 a second through.

**Crowd sounds** (`Audio/soundeffects.txt` "Additionals"; loader 0x490f70, tick every 0.5 s at 0x490340): entry fields
`wav`, `volume` (default 90), `randomweight` (1; 0 = not added), `mintriggers` / `maxtriggers` (1), `virtuallength` s
(1), `virtualvolume` (1), `minfadedistance` / `maxfadedistance` / `maxhearingdistance` (0). Every animation sound that is
actually started and names a GSF sound group is a trigger at its place (0x4900e9 → 0x48fd90). Each 0.5 s: triggers
older than 3 s dropped; points chained within 32 m form a crowd (0x7ac6b0), placed at its box's middle; a crowd sound
with no crowd near is stopped, others move to the nearest crowd within 32 m; for a crowd with at least the group's
smallest `mintriggers`, 1 − the `virtualvolume` of held crowd sounds within 32 m is left, and an entry whose trigger
range fits is drawn by `randomweight` (`rand() × sum >> 15`). Played at pitch 0 (the wav's own speed).
Base data: `sndgroup_combat` 12..100 strikes → `sndgroup_fight_large.wav`; `sndgroup_move_fighter` 10..100 steps → the
march. Quirks: `sndgroup_fight` (war drums) and `sndgroup_move_few_dinos` have entries but no GSF sound names them;
the dinosaurs' step groups have no entries; the second entries of `sndgroup_combat` and `sndgroup_fight` ask for
8 .. 5 triggers (min > max) - never played.

Other: music switches MainMenu < Loading < Background < BackgroundLeveled < Hometown < Region < Trigger < Opening <
Combat < Sequence < GameEnd (highest on plays); Combat after attacks at 3 places within 10 s; Hometown = listener
within 75 m of a home object for 5 s (only the Aje resource collector is one). Ambience state = ground material (0-8)
+ 9 × (camera > 12 m above ground) + 18 × day.

## 7. Tech tree and filters

* **Visibility**: filters *add* to an action's visibility. The server allows an action with visibility > 0; MIRAGE's
  command bar shows only exactly 1 (CommandBar.usl `GetSubValueI("visibility", 1) != 1` → skip). MIRAGE counts with
  negative start values: `final_stage` starts at −1, its `age_5` adds 1 and `xenage/epoch_six_enabled` adds 1.
* **MIRAGE's game-option filters** are added at the start for all four tribes (StartLocation.usl `MirageInitialized`):
  `xenage/epoch_six_enabled` (MaxEpoch >= 6), `titans_disabled` (RemoveTitans: titans, pads, moves −2 and disabled,
  `ai_cap` 0), `flying_disabled` (FlyingEnabled off: pteranodons, cargolifter, dirigible, flying helicopter −2 and
  disabled, jetpack `jetfly_on` −1, zeppelins' `flying_unit` false, menu positions moved up), with Portals and the warp
  gate off `<tribe>_portal_terminus/portal_terminus_visible`. MinEpoch / MaxEpoch 2-5 enable the epoch upgrades /
  AntiActions of `aje_resource_collector`, `hu_fireplace`, `ninigi_fireplace`, `seas_headquarters` (StartLocation.usl:786-826).
* `flying_disabled` also "adds" to the menu position of `ninigi_ninigi_helicopter`, a misspelt path: it creates a node
  holding nothing but a location (a phantom action).
* What `final_stage` unlocks: SEAS great cannon (1500 wood, 1000 stone) and the Scorpion's launch pad (3000 food, 2000
  wood, 1000 stone), Norse colossus pad, Dragon Clan pirate-boss pad / King Kong, Dustrider catapult rex (huge-farm
  mode). The garage's `seas_scorpion` and `hu_colossi` stay hidden on purpose: titans come from the pads.
* **Player vs local filters**: player filters change every object of the player (the Norsemen's
  `/Actions/Hu/Upgrades/hu_palisade/hu_small_wall` turns every palisade into a small wall); `personal` upgrades end in
  a local filter on the one object (the Dustriders' `aje_bone_palisade/clay_wall`). A wall filter writes the **class
  name as the gfx**; the engine's `WallClassChanged` ((C)0x48f3b0, from CWall / CGate.UpdateGfx) then makes the piece
  that class.
* **Spawn class `class§buildup`** (trigger spawn lists): the unit's filters are cleared and
  `/Filters/<tribe>/Upgrades/<class>/<buildup>` is switched on in its own tree. Used in mission 2 (no effect),
  missions 4 and 14 (`hu_wild_boar§wild_boar_rage`: ×1.5 damage for good), mission 12 (`special_eusmilus§global`: no
  such filter). See §15 for MIRAGE master's encoding problem with `§`.
* **Research time**: `Action.usl:346-382` treats paths containing `Actions/Upgrades/` or `Actions/Invent/` as research -
  no tech tree path has that form (`/Actions/<Tribe>/Upgrades/...`), so the computer player's **build** time factor
  also applies to research and the research factor is dead.
* MIRAGE's tree vs base for the Aje farms: medium / huge farm modes need no epoch (base: 2 / 3); MIRAGE adds `personal`
  / `supply` flags.

## 8. Buildings, walls, gates, limits

* **Wall grid**: 8 m tiles, one 16-bit word per tile (WallMap; CalcWall core 0x7e26c0 / (C)0x587080, Placeable
  0x7de030, GateFits 0x7dd600, FindPath 0x7e0730). The tile word stores a wall class of at most 3.
* **One-tile gate** (the shipped `NewGates`): tile empty or an own wall piece, wall pieces on both sides along an axis,
  free flanks, the two pieces at most 2 levels apart; axes tried NW-SE, N-S, NE-SW, E-W. The gate takes the strongest
  class of its tile and wings; its wings send their damage to it (B:2266).
* **Campaign maps use old two-tile gates** (centred on a tile edge / corner: mission 1 (644, 384) x mod 8 = 4). Destroyed,
  they leave a two-tile gap the one-tile rule never fits; the old placement is switched off, so the original refuses
  a new gate there.
* **Unfinished gates are holes** (base and MIRAGE before mirage_gate_blocking): `CGate.OnInit` sets AUTO but the call
  telling the pathfinder is commented out; it learns the state only when the gate is finished.
* **Building limits** (MIRAGE, option BldgMaxCapacity, default off): `max_quantity` of the tree node (towers 10, temples
  5, walls 300, Tesla machine 4, jail 1 …); Aje farms count per type (`max_quantity_<type>`, BLDGConditionsMgr.usl);
  sites count from placement (Building.usl OnInit); queued / running upgrades with `check = '1'` count toward their
  `product_name`. The base tree has none.
* **Bridges have no walk surface**: every `*bridge*` class is a `CDecoColObj` whose OnInit only adds a pathfinder blocker
  (its GSF collision boxes). Units walk on terrain the designers raised under the deck (mission 9
  `jungle_river_bridge_in/_out` at (589, 386-405) over a 23 m strip). Mission 13's `hcl13_bridge_01` and mission 5's
  `hc_bridge_*` are scenery over blocked ground. `pt_footbridge_*` (mission 6) block their whole length. MIRAGE: an
  object loaded with attributes blocks only if they say `blocker` true.
* Fake building corpses (MIRAGE, `mirage_bldg_corpse`); MIRAGE turrets `SetTurret` in `SetReady` (mg nest, cannons),
  not in OnInit.
* Gates on campaign maps: mission 16's `seas_endboss_gate` (MIRAGE: listed without `gate = 1`) / `seas_hq_gate`
  (MIRAGE: `CGateFOWVIsible`) are opened by OnInit; the only gate actions are three Close Gate locking the Scorpion arena.

## 9. Pathfinding and the map's walk data

* Path finder `FindPath` 0x9029b0; layers chosen at 0x40e930, derived from the base maps at 0x9027d0. 1-bit cell maps:
  create 0x8d4090, set 0x8d2f90, grow / erode 0x8d4160, flood 0x8d4850.
* The `.ula` `PaFi` chunk = the editor's land walk grid (2 m cells; no steep slopes, no water; placed objects not in it).
  The PaFi grids **per start location are not limited** to ground reachable from that start (mission 9: every start's
  grid includes the Dragon Clan basin); they differ from the first grid only by placed objects.
* Only walls have a walkable top (the wall map's `/Walkable` config).
* Flying: the engine's `CActFlight` (`FM_Flight`, `FM_Land`, `FM_DiveCombat`) - straight lines, no way finder.

## 10. Computer players

* **One AI tick = 0.2 s.** Think waits (ticks; W = difficulty wait, 100 / 25 / 10 at d 0 / 4 / 9): control module d <= 5
  `W + r(7)`, d >= 6 `20 + r(7)`; build list `W` (list rebuilt every 6th think); idle workers every tick; strategy
  (DisturbAttack) `W + 20 + random % 20`; standing army `5 + r`.
* **Types** (ServerApp.usl:440-487): `ai_Dodo` 2, `ai_Giraffe` 6, `ai_Schnecke` 10, `ai_Mikrobe` / closed 14; the
  `_easy/_medium/_hard` types 3-5, 7-9, 11-13 exist but nothing selects them. Behaviours (CM:285-552): Dodo (aggr.
  100), Giraffe (50, walls, towers, close start), Schnecke (10, risk 1.3), Turtle (Schnecke that never attacks),
  FightOnly, Mikrobe (`SetPaused(true)`), `Singleplayer_L3_*` / `_L8_*` (unused by the shipped campaign).
* Value tables `<Behaviour><Class>.txt` (e.g. `DodoEasy.txt`) do not exist - only `Dodo.txt`, `Giraffe.txt`,
  `Schnecke.txt`, `Singleplayer.txt`; the engine falls back (inferred: to the file without the class).
* **Campaign**: every AI slot is `ai_Mikrobe` (asleep) until a trigger `AIBV` wakes it; its difficulty is the level's
  `PlayerSettings/Player_N/Restrictions/Base/AI_Difficulty_Easy|Medium|Hard` (defaults 1 / 4 / 8). On campaign maps the
  cheat manager is always enabled (CM:300). The Norsemen and the Dragon Clan have no `Units.txt` army tables: no
  standing army in the campaign.
* **Cheat manager** (`CAiCheatMgr`, AiImprovementMgr.usl):
  * `SpawnResources`: skirmish maps Easy 5 / Medium 20 (skulls 3) / Hard the asked amount; other maps the amount + 10
    (skulls as asked). The control think gives skulls only at d 7-9.
  * `SpawnUnit` (missing units of a squad after the BuildSquad timeout 500 / 400 / 300 ticks = 100 / 80 / 60 s): **only
    on skirmish maps and only at d = 9**, within the pyramid + 5 / 2 / 1 / 1 / 0 extra slots, workers only below 15,
    no trade units / fishing boats, at a finished producing building with a visible build action (remake: docs/AI.md
    "The cheat manager's free units").
    In the campaign nothing spawns; the wave leaves with the units it has plus 0-4 / 0-6 / 0-8 pool units.
  * Weapon cycle × 0.95 / 0.9 at d 8 / 9 (§2); fight factors (§2); free epoch catch-up at d >= 6 on skirmish maps (the
    `age_N /AI_Help` suffix makes `CheckConditionsAndPay` true without checks or cost, RequirementsMgr.usl:373-377).
* **What MIRAGE changes** (master b320f9bb):
  * `m_bCustommap` = `CMirageAIMgr.CheckCustomMapAISetting(level, player, "CampaignAI")`: `CustomMapSettings.txt`
    `Levels/<level>/Custom/Players/Player_N/CampaignAI`. Set → the player behaves as on a campaign map (no
    `SpawnUnit`, campaign gift branch, the goals' `m_bMultimap && !m_bCustommap` skirmish rows off).
  * `SpawnUnit` returns at once when `!m_bMultimap || m_bCustommap` (base: `!m_bMultimap`); still d = 9 only on skirmish.
    Excludes trade units and boats incl. `seas_trade_dino`, `seas_carrier`; counts `virtual_units_<level>` player
    attributes against the pyramid. Its non-skirmish branch (+15 / +12 / +10 slots) is unreachable (inferred).
  * `SpawnResources`: on `_CU_MP_6_BFPW_HIGHLAND` / "BfPW - Highland" / "BfPW - HL" no skulls.
  * Gifts in its own form (AiModuleControlDefault.usl Think:519-587): per difficulty iFood / iWood / iStone / iIron ×
    an epoch factor (1, 1.5, 2, 2.5, 3, 4), not for player 0, from difficulty 2.
  * `GetAICheatModifier` (FO:9747, same values) is applied to the duration (FO:9654), ranged and melee durations
    (10577, 10617, 10742) and build-up weapons (BuildUpBase.usl:1136).
  * `CMirageAIMgr` lets the goals follow the game options: hero pool, free specials, supply, epoch six (BV:406-409:
    `final_stage` requested at epoch 5, the sixth stage's buildings once it is there), titan slots, flying
    (GeneralAttack:647: 2 flyers join every main-village squad from epoch 3, difficulty >= 4), remove titans,
    `AIOptions/walls`, leveling (`DwnLvlSwitch > 0`, Kindergarten). BldgMaxCapacity and Portals are not read by the AI.
    (Remake: docs/AI.md "MIRAGE: the AI follows the game options".)
  * Bug: see §15 (`flyers_enabled` checks RemoveTitans).
* Unused base code: `CheckForWorkerSupport` (GV:1142) and `ShouldRetreat` (GA:1370) are never called.

## 11. Campaign and triggers

* **Trigger semantics**: enabled + folder active = listening; every invalidation while true fires (no edge detection);
  `once` = disabled when it fires (re-enabled by `TRIG`); actions run in a FIFO queue at the end of the step; a trigger
  disabled in the step it was queued still fires; not compiled = does not exist; empty expression = AND of all; no
  condition = never. `random` = one action at random; `by_difficulty`. Variables: `set` keeps the string, `+ - * /` are
  integers, `/ 0` changes nothing. Level timers: expiry pushes every `TIMR` condition, also of disabled triggers.
  `GAOV`: the AI is told at once, the defeat 4 s later, a second one ignored.
* Poll intervals: `REGN` / `DEAD` on a region 0.5 / 1 s, `OBJP` 0.25 s, `PLYR` / `TECH` 0.5 s, `BLDG` 1 s, `SGHT` 2 s,
  `UNTT` 0.3 s, `CHCK` 0.5 s. Region events (CreatePersonalRegion) are a check every 0.25 s.
* **TECH action** filter lines are `player|on|path` (e.g. `0|0|/Filters/AntiActions/Hu/Upgrades/hu_fireplace/age_4` =
  player 0, AntiAction off → epoch 4 allowed). Mission 2 allows the Norse epoch 4 after the mammoth quest; mission 4's
  `Ix04: last_minute_emergency_hotfix` (25 Aje workers in a region) lifts the Dustriders' age_4 / age_5 caps.
* Level-start epoch caps of the player (start AntiActions): missions 1, 2, 9 → epoch 3; 4 → 3 (5 after Ix04); 5 → 1;
  6, 10 → 4; 7 → 5.
* **Loose ends in the campaign data** (223): 67 `ACDO` name objects not on the map (+14 found only by name), 20 `TRIG`
  switch triggers that are not compiled, 9 name missing triggers, 17 `ADGR` / 14 `AIAM` list missing objects, 6 `QUES`
  missing quests, 4 start locations and 4 `QMRK` invalid, 2 `REGN` / 2 `BLDG` missing regions.
* Start times: mission 0 6:00, 5 15:00, 11 18:00, 16 8:00, others 12:00; only mission 8 has weather (light snow).
* The campaign zeppelin `FlyingTrader_Obj` (`CFlyingTraderObj`, Vehicle.usl:852-890): not hittable or selectable, no
  population, straight 3D line at the order's speed capped at `maxspeed` 3 (MIRAGE 4). The multiplayer `CFlyingTrader`
  is switched off.
* `OCPY chk_highlight` with `highlight_time` > 0: a ring under the object for floor(time) s.
* Mission flow: mission 4 → 5 is the original's own rule (CampaignMgr.usl:159-167), not 4 → 4-1.
* MIRAGE mission 3 (Amazons, `SP2Attack_1` = 3-4 `aje_dilophosaurus`): only a farm in small-farm mode makes them; with a
  full first pyramid row nothing can queue them and the wave leaves after the timeout with pool units only.

## 12. Map files (.ula)

* A container of zlib blocks holding a `SURF` chunk tree. Strings are **Windows-1252**.
* Map space is left-handed: x east, **y south**, z up, metres from the north-west corner. Heading of a stored quaternion
  q: rot = −2 atan2(qz, qw).

| chunk | content |
|---|---|
| `LInf` | key/values (LevelName, MapName, Setting, MaxPlayers …), description tree (`Root/Base/StartTime`), 200×200 RGBA preview, default camera (0.785 rad) |
| `Terr` | width / height (m), water level, height grid (float32, 2 m), materials (0..7 per 4 m cell), terrain setting |
| `PaFi` | land walk grid (2 m cells), per start location (§9) |
| `Objs` | objects: type tag, name, class, position, rotation, owner (0xff = none), attributes |
| `IOMG` | plant / decoration instances |
| `Frst` | forest blocks of 31 item bytes (the 32 layouts are in the exe); nothing at or below 16 m |
| `Rgns` | regions: rectangles / ovals, enabled flag |
| `Trgr`, `Ques`, `DlgS`, `AI` | triggers, quests, dialogues, AI data |

* A mission's level name is `Base/LevelName` ("Single 01"; MIRAGE's SEAS copies of the Booster Pack missions keep
  "Single B41" etc.). Scripts that look a level up by name replace spaces with `_`.

## 13. MIRAGE: game options and per-level settings

**Defaults** (`Game/UI/Mirage_UI_scripts.usl` `SetDefaultStuff`, the adjust windows, OptionsMenu.usl): every checkbox
off except AllowThrow, OldDisembark, AuraSharing, AllyBuildup (on); TransportHealing 60, DwnLvlSwitch 0, MinEpoch 1,
MaxEpoch 6, HQTimer 5 min, WarpGateTimer 5, DominationUpDownTimer 10, PauseLimit 3, SandGlass 0, start time 8:30:00.
Config paths `Server/GameplayOptions/<name>`; `AllowAlienCommands` is `Server/AllowAlienCommands` (path fixed in the exe).
Single player: the server reads the config (`MirageSrvMgr.usl`, `!IsMultiplayer()` → `xConf.GetSet…`); multiplayer:
the game attributes the lobby set.

Meanings (from `Locale/*/Texts/mirage.ltf` and the code): TransportHealing = % healing of healers on an open transporter
(`m_fTHF`, character.usl:748; > 0 = allowed); DwnLvlSwitch 0 none / 1 heroes / 2 all units, not below captainlevel − 1 /
3 no limit (FO `IsLevelAcceptable`); DivideSkulls, BonusSkulls (§2); CorpseDamage = big corpses hurt enemies they fall
on; HeroPool = one player per hero, random hireable heroes; TechtreeSteal = use other tribes' trees; MultiTribe = other
tribes' workers from the start; TitanSlots = titans + 2 transport slots; UseSupply = supply system instead of the
52-unit limit; AllowAlienCommands = control allied units; UseFM = finishing moves; AttackInFOW = long-range area
fighters may attack some targets in the fog; OldDisembark = the old, less safe disembarking; HQTimer −1 = off.

**Per-level settings** (`Game/misc/CustomMapMirageSettings.txt`): applied **only in the multiplayer lobby**
(`LevelSettingsPage.UpdateCustomMapMirageSettings` → `CMirageClnMgr.UpdateCustomMapAttribs`, user's values backed up
and restored; the adjust windows lock the controls). The single-player campaign never reads the file. The Single_*
entries serve the multiplayer campaign (`CustomMapSettings.txt` `MultiplayerCampaign = 1`). Differences from the
defaults:

* all 19 campaign levels listed: UseFM 1, HQTimer −1; all but 05 / 11 / 16 / B42: TransportHealing 40.
* FlyingEnabled 1: 01, 03, 04, 06, 07, 09, 10, 13, 15, B41. BldgMaxCapacity 1: 03, 04, 07, 08, 12, 15. HeroPool 1: 06, 15.
* MaxEpoch: 01 / 02 / 09 / 10 → 4, 04 → 3, 07 → 5, 05 / 11 / 16 / B42 → 1.
* DwnLvlSwitch 1: 01, 04, 09, 11; 2: 02, 03, 05, 06, 13, B42. DivideSkulls 1: 01, 05, 11, 16, B42.
* 05, 11, 16, B42 (the MaxEpoch 1 levels): CorpseDamage 1, BonusSkulls 1, AllowAlienCommands 1 (master; installed Dec 2024: 0);
  11 also TitanSlots 1 and UseSupply 1. TechtreeSteal 1: B42, B43; MultiTribe 1: B43.
* Installed (Dec 2024) vs master: only the AllowAlienCommands values above and `Prison_Island_[CUSTOM]` /
  `Living_ruins_[CUSTOM]` renamed to `Prison_Island` / `Living_ruins`.

Notes: **MaxEpoch 1 does nothing** in StartLocation.usl (only 2-5 add AntiActions) except switching the sixth stage off.
Most MaxEpoch values equal the level's own start caps (§11); mission 4's 3 is lifted by the level's own trigger `Ix04`,
which switches the same filter paths off (inferred). The `Single_B4x` entries also hit MIRAGE's SEAS copies
(`single_b4x_seas`: the same level name).

## 14. Bugs and quirks - base game

* `Server/FirstStrike/MaxHitDist` is overwritten with 0.5 by the engine (§2).
* `SetModuleBehavior` writes the wrong player slot (§1).
* Unfinished gates are pathfinder holes (`CGate.OnInit`, §8).
* Damage cache ignores splash factors and buff changes; `EndDmg` unscaled; `MinDmg` unused (§2).
* AI research time factor dead, build factor applies to research (§7).
* `_dusttrail` GSF effects and MaterialEffects GFX do nothing (§4).
* Crowd-sound entries with min > max triggers never play; war-drum group never triggered (§6).
* Tree crowns cast no shadow; `bSkipBillcloudShadow` never fires (§5).
* Value tables `<Behaviour><Class>.txt` missing; `_easy/_medium/_hard` AI types unused (§10).
* The base installation's script folder lacks several task classes (Harvest, Mine, GetFood, GetCorn, GetUnlimited,
  BuildUp, Repair, HealUnits, Feed, Sleep, PickUp) and `TransportObj/` - MIRAGE's repo has copies.

## 15. Bugs and quirks - MIRAGE (verified on master b320f9bb)

* **`Server/misc/ActionFactory.usl` is UTF-8** (the `§` of SpawnGroup's `class§buildup` split at :5158-5160 is the
  two bytes C2 A7); 28 other non-ASCII scripts in master are Windows-1252 and the maps' trigger strings are cp1252. If
  the USL compiler reads source as cp1252 (inferred: 8-bit strings, .ula text is cp1252), the literal is `Â§`, `Find`
  never matches, and `CreateObj("hu_wild_boar§wild_boar_rage")` fails: no unit spawned (missions 2, 4, 12, 14
  spawn lists). Fix: save the file as Windows-1252 (or build the separator from its char code). Also UTF-8 (only
  comments / tooltips affected): `Building.usl`, `Animal.usl`, `LoadGameMenu.usl`, and `Game/UI/ChatWindow.usl`, whose
  whisper prefix `"§"` (OnWhisper type 1, :572) would then show as `Â§` in the lobby chat (inferred).
* **Swimming nests** (`Server/classes/animals/Nest.usl` `CSwimmingNest.OnInit` :1529-1538): for aggressive species with
  `spawn_amount == -1` it computes `spawn_max × SPAWN_WAVE_AMOUNT (3)` and writes it into **`spawn_max`** (the nest's
  capacity), leaving `spawn_amount` at −1: the nest holds three times the animals, forever - meant was three broods
  (`spawn_amount = 3 × spawn_max`). The `if(iSpawnAmount != iEggsAmount)` test is always true.
* **AI `flyers_enabled`** (`Ai/goals/AiGoalMinistryOfDefense.usl` `SpecialAllowed` :1072): checks `RemoveTitans()`
  instead of `!FlyingEnabled()`. It shows only with the option FreeSpecials (without it a row with a value passes only
  when a hero of that name is at level 3+, which `flyers_enabled` never is): then with flying off the AI still asks for
  pteranodons (`Ai/settings/Hu/UnitsMedium.txt`), which `flying_disabled` refuses (inferred: wasted requests), and with
  titans off it drops flyers even when flying is on. Fix: `if(p_sRequirement=="flyers_enabled"&&!CMirageAIMgr.Get().FlyingEnabled())`.
  The remake implements the intended test (docs/AI.md).
* **MaxEpoch 1 is a no-op** (StartLocation.usl:811 `iMaxEpoch < 6 && iMaxEpoch > 1`).
* **Server default MaxEpoch 5 vs client 6**: `MirageSrvMgr.usl` EpochSix reads `GetSetI("Server/GameplayOptions/MaxEpoch",
  5)` and the client `SetDefaultStuff` 6; `GetSet` writes its default when the key is missing, so on a fresh config the
  first reader decides (inferred).
* **CustomMapMirageSettings only in the lobby** (§13); the client loads it from `/data/base/scripts/game/misc/` (found
  through MIRAGE's file replacement).
* Special moves (first found in 2.6.7, still in master): the Norsemen's automatic Kick / Quake / Roar never fire
  (`character.usl:331, 388, 580`: `!= "hu_warrior" || != "hu_jetpack_warrior"` is always true); the medic's parasite
  never expires (`Mirage.usl` CParasite.HandleEvent :14961 / :14969 both test `TRACKER_TIMER`, the second should be
  `TIMER_PARASITE_LIFE`); a Shift-queued deafening roar has radius 0 (`Miracle.usl` CCustomTask 4-argument `Init`
  :6185 never sets `m_fRealValue`); the monk's peace gong works on enemies (`character.usl:3246` `bFriend` computed, not
  used); the walking bomb's kills count for the victim's owner (`Miracle.usl:1741-1745`: `xSacrificer` is set only
  when already valid). More (2.6.7, not re-checked here): pw-exporter `remake/docs/MIRAGE_BUGS.md`.
