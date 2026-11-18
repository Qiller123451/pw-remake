// The fields of entities, tasks and players that the combat, effects, healing, finishing, items and game-over systems
// keep (the JS put them on the objects as it went: u.prevTask, u.shoutT, V.lastDamager, st.poison ...).
//
// Some are set by other systems or by the campaign and only read here - they live here because combat is their
// first reader:
//   Entity.Disarmed       scripting.js setDisarmed (MIRAGE WeaponRemove)
//   Entity.IsNest         campaign/setup.js (a nest of a campaign map: attacked on an order only)
//   Entity.ParentGate     construction.js (a gate's wing passes its damage to the gate)
//   Building.InvulnT      scripting.js setInvulnerable (Unit.InvulnT is in the core)
//   Unit.WallDef          wallwalk.js setOnWall (Defence_On_Wall)
//   Unit.RageT / RageHit  moves.js (a mammoth's stampede ends with its first real attack)
//   Player.Hq             the skirmish setup (main.js: the start position's head quarter index)
//
// Ported from remake/src/game/systems/combat.js, effects.js, healing.js, finishing.js, items.js (pw-exporter 98c7703).
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public abstract partial class Entity
{
    /// <summary>who hit it last (an entity; the finishing move and the computer player read it)</summary>
    public Entity? LastAttacker;
    /// <summary>the player who damaged it last: gets the skulls (OnKill FO:3950)</summary>
    public Player? LastDamager;
    /// <summary>the damage of the last hit taken (the damage cache a finishing move looks at)</summary>
    public double DmgCache;
    /// <summary>buildings and ships: the damage stage of the last explosion (0, 1 at 50 %, 2 at 25 % hit points)</summary>
    public int DmgFx;
    /// <summary>MIRAGE WeaponRemove (scripting.js setDisarmed): no attacks</summary>
    public bool Disarmed;
    public double DisarmUntil;
    /// <summary>a nest of a campaign map (campaign/setup.js): only attacked on an order</summary>
    public bool IsNest;
    /// <summary>a gate's wing: its damage goes to the gate (B:2266; construction.js sets it)</summary>
    public Building? ParentGate;
    /// <summary>the last call for help (ShoutForHelp throttle)</summary>
    public double ShoutT = -99;
}

public sealed partial class Unit
{
    /// <summary>where the last blow came from, seen from the unit (FightingObj m_dwLastHitDirection, FO:4429:
    /// GetDirection of the attacker): 'f' front, 'b' back, 's' a side, '\0' none yet - a character's death clip
    /// (CCharacter.CreateCorpse); SelfKill: killed by its own player (Del: "Kill" -> DiePerHarakiri)</summary>
    public char LastHitDir;
    public bool SelfKill;
    /// <summary>
    /// The hit reaction (FightingObj.ProvideDmg FO:4423-4446): a melee blow of at least 1 hit point on a character or
    /// animal that was not thrown, not on a wall, not in a trap, invokes the engine's overlay clip
    /// EID_HitReactionOVLAnim_F / B / L / R by the side of the blow. The view plays it over the running clip
    /// (AnimController.HitReaction); the simulation only counts it: HitReactN goes up by one, HitReactSide is 'f', 'b',
    /// 'l', 'r', HitReactT the game time. View only - nothing of the world reads them.
    /// </summary>
    public int HitReactN;
    public char HitReactSide;
    public double HitReactT;
    /// <summary>the fight clip of its last swing (FightingObj GetFightAnim: m_xCurrentFightAnim, chosen by
    /// GetNextFightAnim) - a victim's beheading asks the killer's (CCharacter.DiePerHeadOff)</summary>
    public string? FightAnim;
    /// <summary>a beheaded corpse's head (CCharacter.DiePerHeadOff, character.usl:2252-2270): the model of the class
    /// "&lt;class&gt;_head" (a plain CGameObj, a static mesh) at the corpse's place and turn, gone with the corpse
    /// (CCharacterCorpse's destructor deletes it); null = none</summary>
    public string? Head;
}

public sealed partial class Building
{
    public double LastHit = -99;
    /// <summary>the level designer's invulnerability (scripting.js setInvulnerable) - Unit.InvulnT for buildings</summary>
    public double InvulnT;
    /// <summary>a target the player ordered the tower to shoot (aimTowers): kept while it lives</summary>
    public Entity? ForcedTarget;
    /// <summary>the tower's current target</summary>
    public Entity? TowerTarget;
    /// <summary>gameover.js: does it produce units (CPlayer.CanProduceUnit), null = not asked yet</summary>
    public bool? Producer;
}

public sealed partial class Unit
{
    /// <summary>a worker's task before an automatic fight (resumed after it)</summary>
    public UnitTask? PrevTask;
    public Entity? LastTarget;
    /// <summary>targets the unit found no way to: left alone until the time (findTarget)</summary>
    public OrderedMap<Entity, double>? Unreach;
    /// <summary>moves.js: a mammoth's stampede; RageHit: it ended with a real attack</summary>
    public double RageT;
    public bool RageHit;
    /// <summary>wallwalk.js setOnWall: the ranged defence of a unit on a wall's walkway (Defence_On_Wall)</summary>
    public double WallDef;
    /// <summary>the item carried (items.js: one at most)</summary>
    public Item? Item;
    /// <summary>healing.js: the healer heals (its kit glows)</summary>
    public bool Healing;
    /// <summary>kennel animals, hatchlings: they stay within this of their home (animals.js; buildings.js and combat.js set it)</summary>
    public double HomeRadius;
    /// <summary>items.js IgnoreSlope (SetIgnoreSlope): no unit of the game is stopped by a slope in the remake</summary>
    public bool IgnoreSlope;
    /// <summary>finishing.js: the last victim of its finishing moves</summary>
    public Unit? LastFinishVictim;
    /// <summary>the finishing move this unit is the victim of (finishing.js; the core's Unit.Finish holds it)</summary>
    public FinishState? FinishMove => Finish as FinishState;
}

public sealed partial class UnitTask
{
    /// <summary>attack: the next look at the enemies (TIMER_AGGRO), when a better one was first seen</summary>
    public double? ExamT, ChangeT;
    /// <summary>attack: following the enemy (FollowEnemy) - time, best distance, time without progress</summary>
    public bool Chasing;
    public double ChaseT, NoProg, ChaseMoveT;
    public double? BestD;
    /// <summary>attack: ways not found to the target</summary>
    public int Fails;
    /// <summary>a walk back to the post after a fight</summary>
    public bool BackToPost;
    /// <summary>heal: the next glow (healfx)</summary>
    public double FxT;
    /// <summary>pickup: walking to the item</summary>
    public bool Walking;
}

/// <summary>A poison slot (combat.js applyPoison): damage per tick, ticks left, the next tick, who poisoned.</summary>
public sealed partial class PoisonState
{
    public double Dmg;
    public int Left;
    public double Next;
    public Entity? By;
    public PoisonState Copy() => (PoisonState)MemberwiseClone();
}

/// <summary>Effects of auras on a unit (effects.js auraUpdate); the core has the rest.</summary>
public sealed partial class AuraState
{
    /// <summary>aje_skull_protector: killing the unit gives no skulls</summary>
    public bool SkullProtect;
    /// <summary>Schliemann (Kleemann): skulls x this (0 = none)</summary>
    public double Kleemann;
    /// <summary>hero bonus entries, one per name: name -> (bucket, value) - two Coles do not add up</summary>
    public OrderedMap<string, (string Bucket, double V)>? Bon;
}

public sealed partial class Player
{
    /// <summary>Player.AddScalps: the skulls modifier (the MoreScalps item), 1 = none</summary>
    public double ScalpMod = 1;
    /// <summary>gameover.js: it had a head quarter once (the 5 minute timer starts only then)</summary>
    public bool HadHQ;
    /// <summary>the head quarter (start position) index of a skirmish (main.js); the defender plays on 0</summary>
    public int? Hq;
}

/// <summary>The projectile's values at the shot (CArrow.Set: the attacker's values when it shot) and its delay.</summary>
public sealed partial class Projectile
{
    public ProjectileSnap? Snap;
    /// <summary>a bunker's volley: the arrow leaves after this (s)</summary>
    public double Delay;
    /// <summary>a special arrow (World.SpecialShot): DELAYED[Impact](world, ImpactArgs) on impact instead of the weapon's damage</summary>
    public string? Impact;
    public Args? ImpactArgs;
}

public sealed class ProjectileSnap
{
    public double Dmg, EndDmg, Splash, Ap, Size;
}
