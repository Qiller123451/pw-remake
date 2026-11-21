// The loose objects of the JS entities as classes (docs/PORTING.md "Loose JS objects become classes"):
//   UnitTask  u.task  {type: 'gather', node, phase ...}: what a unit does; one class with the fields any task uses
//   Order     o       {type: 'move', x, z, target, then ...}: an order (World.Order, Commands), also a task's `then`
//   Status    e.st    timed states, auras, bonus buckets, camouflage layers, poison (systems/effects.js newStatus)
//   Carry     u.carry {res, amount, look}
// All are partial: a system port adds the fields only it uses in its own file (src/Sim/Systems/<X>.cs:
// `public sealed partial class UnitTask { public double Foo; }`).
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>What a unit does (u.task). Type: idle | hold | move | attackmove | attack | gather | build | roam | flee |
/// special | board | fountain | pickup | wall | climb | dock | finish | trade | entrench | cast | heal | support ...</summary>
public sealed partial class UnitTask
{
    public string Type;
    /// <summary>given by the player (not automatic)</summary>
    public bool User;
    public string? Phase;
    public Entity? Target;
    /// <summary>the next order on arrival (move / attack-move), a finishing move's queued order (Next)</summary>
    public Order? Then, Next;
    public double X, Z;
    /// <summary>attack-move: the walk to go on with after the fight [x, z]</summary>
    public double[]? Amove;
    /// <summary>automatic (an attack started by an attack-move scan)</summary>
    public bool Auto;
    /// <summary>at walking pace (mission scripts)</summary>
    public bool Slow;
    /// <summary>the gait of a scripted walk (the original's /Speed=n: 1 slowest .. 4 run, Unit.GaitSpeed); 0 = the unit's own</summary>
    public int Gait;
    /// <summary>the group walk (World.PullGroup): units of one order share the id; 0 = none</summary>
    public int Wp;
    /// <summary>the zeppelin's flight: height to reach (map z), speed; Alt0 / D0: its height and distance at the start</summary>
    public double? Alt, Alt0;
    public double D0, FlySpeed;
    public bool ForceAttack, Force;
    /// <summary>hold ground (heal tasks of a unit on hold)</summary>
    public bool Hold;
    /// <summary>the task to go back to (a healer's attack move, a wall walk's waiting task)</summary>
    public UnitTask? Back, Resume;
    public string? Res;
    public ResNode? Node;
    public Building? Building, Farm;
    public Entity? Drop;
    public Entity? Victim;
    public object? Slot;
    public ActionDef? Action;
    /// <summary>idle: when to play a victory animation</summary>
    public double? VictoryAt;
    public double Until, Wait, T;
    public int Tries, I;

    public UnitTask(string type) { Type = type; }
    public static UnitTask Idle() => new("idle");
    public override string ToString() => Type + (Phase != null ? ":" + Phase : "");
}

/// <summary>An order (World.Order): type move | attackmove | patrol | attack | stop | hold | gather | build | repair |
/// deliver | heal | board | dock | fountain | pickup | use | climb | unload | trade | rallyJob | kill.</summary>
public sealed partial class Order
{
    public string Type;
    public double X, Z;
    public Entity? Target;
    public Order? Then;
    public bool Slow;
    /// <summary>move: the gait of a scripted walk (WalkAction / waypoints "| speed", FightingObj HandleWalkCommand
    /// /Speed=n: GoTo at that gait, not the unit's default speed); 0 = the unit's own</summary>
    public int Gait;
    /// <summary>the group walk id (World.NextWalkGroup), 0 = none</summary>
    public int Wp;
    public double? Alt;
    public double FlySpeed;
    /// <summary>patrol: the points and the index of the next</summary>
    public List<(double X, double Z)>? Pts;
    public int I;
    /// <summary>automatic (not the player's)</summary>
    public bool Auto;
    public bool Force;
    /// <summary>the queued wall tiles stay (G.placeWall's own first step)</summary>
    public bool KeepBuildQueue;

    public Order(string type) { Type = type; }
    public static Order Move(double x, double z) => new("move") { X = x, Z = z };
    public static Order Attack(Entity t) => new("attack") { Target = t };
    public Order Clone() => (Order)MemberwiseClone();
    public override string ToString() => Type + (Target != null ? " " + Target : Type is "move" or "attackmove" ? $" ({X:0.#}, {Z:0.#})" : "");
}

/// <summary>Bonus buckets of a unit (FightingObj: DAMAGE, MELEEDAMAGE, RANGEDAMAGE, DEFENSE, RANGEDDEFENSE, RANGE, BLDGDAMAGE).</summary>
public sealed class BonusBuckets
{
    public double DAMAGE, MELEEDAMAGE, RANGEDAMAGE, DEFENSE, RANGEDDEFENSE, RANGE, BLDGDAMAGE;
    public double this[string k]
    {
        get => k switch { "DAMAGE" => DAMAGE, "MELEEDAMAGE" => MELEEDAMAGE, "RANGEDAMAGE" => RANGEDAMAGE, "DEFENSE" => DEFENSE, "RANGEDDEFENSE" => RANGEDDEFENSE, "RANGE" => RANGE, "BLDGDAMAGE" => BLDGDAMAGE, _ => 0 };
        set { switch (k) { case "DAMAGE": DAMAGE = value; break; case "MELEEDAMAGE": MELEEDAMAGE = value; break; case "RANGEDAMAGE": RANGEDAMAGE = value; break; case "DEFENSE": DEFENSE = value; break; case "RANGEDDEFENSE": RANGEDDEFENSE = value; break; case "RANGE": RANGE = value; break; case "BLDGDAMAGE": BLDGDAMAGE = value; break; } }
    }
    public static readonly string[] Keys = { "DAMAGE", "MELEEDAMAGE", "RANGEDAMAGE", "DEFENSE", "RANGEDDEFENSE", "RANGE", "BLDGDAMAGE" };
}

/// <summary>The auras acting on a unit (systems/effects.js auraUpdate: st.aura); the effects port adds the rest.</summary>
public sealed partial class AuraState
{
    public int Warcry, Warpaint;
    public bool Drums, NinigiCauldron, BoarRage, Pennant, Invisible, NoAnimalAggro;
    public double Slowhand;
    /// <summary>Livingstone's drain: the heroes draining this unit</summary>
    public List<Entity>? DrainBy;
}

/// <summary>Poison on a unit (combat.js poisonUpdate); the combat port adds its fields.</summary>
public sealed partial class PoisonState { }

/// <summary>Status effects, auras, camouflage, poison (systems/effects.js newStatus).</summary>
public sealed class Status
{
    /// <summary>timed states: key -> game time until which it lasts (trapped, deaf, trumpet, invul, cyborg ...)</summary>
    public readonly Dictionary<string, double> T = new(StringComparer.Ordinal);
    public AuraState Aura = new();
    public readonly BonusBuckets Bonus = new();
    /// <summary>camouflage layers (entr, disg, aje, trap, vanish ...)</summary>
    public readonly OrderedSet<string> Camo = new(StringComparer.Ordinal);
    public PoisonState? Poison, Poison2;
    /// <summary>why the unit is trapped ("dig", "knock", "hypnosis" ...)</summary>
    public string? TrapWhy;
    public double RevealT = -99;
    /// <summary>is a timed state active? (effects.js active)</summary>
    public bool Active(string key, double time) => T.TryGetValue(key, out var until) && until > time;
}

/// <summary>A carried load (u.carry): resource, amount and its look [link, gfx, walk set] (economy.js carryLook).</summary>
public sealed class Carry
{
    public string Res = "";
    public double Amount;
    /// <summary>[link, gfx, walk set] or null</summary>
    public string[]? Look;
}

/// <summary>A unit set up to shoot (compose.js DEPLOY: the log cannon): walk | down | attack | up.</summary>
public sealed class DeployState
{
    public string State = "walk";
    public double Until;
}

/// <summary>A knockback slide / jetpack jump / lift (world.js unitUpdate).</summary>
public sealed class Knock { public double Vx, Vz, T; }
public sealed class JumpFx { public V3 From, To; public double T, TT; }
public sealed class Lift { public double From, To, T, TT; }
/// <summary>A pending shot (u.pending: fire after a delay). A melee blow of the attack loop decides at the swing whom it
/// hits (Victims; an area blow: the point At) and lands after the delay whether or not they moved (combat.js deviation:
/// FightingObj AttackEnemy calls TakeDmg / Penetrate when the swing starts)</summary>
public sealed class PendingShot
{
    public double T; public Weapon? W; public Entity? Target;
    public bool Melee;
    /// <summary>a shot at the ground (World.FireAt at At: GroundAttack.cs)</summary>
    public bool Ground;
    public List<Entity>? Victims;
    public V3 At;
}

/// <summary>A soul of a fallen unit (Aje shaman: Resurrect).</summary>
public sealed partial class Spirit
{
    public Player? Owner;
    public V3 Pos;
    public double Until;
    public bool Alive = true, Busy;
    public string Name = "";
    public int Level = 1;
}

/// <summary>Jumping on / off a wall (systems/wallwalk.js); the order given meanwhile comes after the landing.</summary>
public sealed partial class WallJump { public Order? Then; }

/// <summary>A ship's wreck sinking (combat.js shipWreck): its model and animation.</summary>
public sealed partial class Wreck { public string? Gfx; public AnimCtl? Anim; }
