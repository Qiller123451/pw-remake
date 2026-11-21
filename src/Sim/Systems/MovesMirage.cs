// The MIRAGE mod's special moves (its own heroes' moves, the moves its upgrades add to the four tribes) and what
// they need besides: timed unit filters, jetpack flight, Babbage's exo suit, the blinding storm, parasites.
//
// Reference: docs/spec/moves_mirage.md (from MIRAGE's Scripts/Server: Hero.usl, character.usl, Animal.usl,
// Vehicle.usl, mirage/Miracle.usl and Mirage.usl). The moves only exist in MIRAGE's tech tree, so with the official
// game's data none of this is ever used. Where MIRAGE's scripts have a bug the remake does what was meant; each of
// those is listed in docs/MIRAGE_BUGS.md (marked "fix:" below).
//
// Moves of MIRAGE that are not here on purpose:
//   Jumpjet_0, sand_trap_bfpw, bfpw_kick/quake/roar   only in MIRAGE's "BfPW" game mode, which the remake doesn't have
//   FeignDeath, tree_camouflage, Hu_Multi_Arrow         switched off by MIRAGE itself (no handler / never researchable)
//   RamAttack                                           no handler; the ram is a weapon swap of the "ram" research
//
// Ported from remake/src/game/systems/moves_mirage.js (pw-exporter 98c7703).
// C# notes: the JS's CAST table is MirageMoves.Cast*; the captain's victory clip of Miyagi's plunder
// (u.comp.playCaptain) is the view's.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using static PwRemake.Sim.World.Moves;

namespace PwRemake.Sim.World;

public static class MirageMoves
{
    /// <summary>unit-local filters of the tech tree</summary>
    public static class F
    {
        public const string fright = "Ninigi/Upgrades/xenage/frightening";      // attacks x1.5 slower
        public const string valor = "Ninigi/Upgrades/xenage/valor";             // attacks x0.9 (faster)
        public const string stim = "SEAS/Upgrades/seas_laboratory/marine_stimpack";
        public const string jet = "Hu/Upgrades/xenage/jet_flying";              // sky weapons, flying stand animation
        public const string rounds = "SEAS/Upgrades/xenage/explosives_rounds";
        public const string pierce = "Special/Upgrades/xenage/PiercingThrough"; // the exo suit's guns: armour piercing 100
        public const string sacrifice = "Special/Upgrades/schliemann_s0/Sacrifice_Bonus";
    }

    // units of these owners count: the caster's own and its allies' / its enemies' (and neutral ones)
    internal static List<Unit> FriendsNear(World W, Unit u, V3 c, double R, string[]? types)
    {
        var res = new List<Unit>();
        W.UHash.Query(c.X, c.Z, R + 8, v =>
        {
            if (!v.Alive || v.Inside || v.Owner == null || u.Owner == null || !u.Owner.IsFriend(v.Owner)) return;
            if (types != null && Array.IndexOf(types, v.Cls) < 0) return;
            if (Math.Sqrt((v.Pos.X - c.X) * (v.Pos.X - c.X) + (v.Pos.Z - c.Z) * (v.Pos.Z - c.Z)) - v.Radius <= R) res.Add(v);
        });
        return res;
    }
    static bool Hostile(Unit u, Entity? v) => v != null && v.Alive && v != u && (v.Owner == null || u.Owner == null || !u.Owner.IsFriend(v.Owner));
    static bool OwnOrAllied(Unit u, Entity? v) => v != null && v.Alive && v.Owner != null && u.Owner != null && u.Owner.IsFriend(v.Owner);
    static V3 At(Entity? t, V3? pos) => t != null ? t.Pos : pos!.Value;
    static readonly string[] AnCh = { "ANML", "CHTR", "VHCL" };

    public const double TWINS_TIME = 60;          // CTwins.STAYTIME
    public const double REANIM_TIME = 25;         // a spirit's reanim_time (FightingObj.usl:12021)
    public const double FLY_HEIGHT = 13;          // CHu FLYING_HEIGHT

    /// <summary>models these moves show (main.js loads them with the map)</summary>
    public static readonly string[] MIRAGE_FX_MODELS = { "fx_dust_twister", "rolling_stone", "fire_molotov", "hu_steam_jet_pack_off", "fx_heal_passive" };

    /// <summary>what upgrades do besides switching filters (production.js completeUpgrade)</summary>
    public static readonly Dictionary<string, Action<World, Entity, ActionDef?>> UPGRADE_HOOKS = new(StringComparer.Ordinal)
    {
        // Babbage's mechanization (Hero.usl:1169 HandleAction "special_mechanization"): he flies up and comes down as the exo suit
        ["special_mechanization"] = (W, e, a) => { if (e is Unit u && u.Name == "babbage_s0") W.Mechanize(u); },
    };

    static void Add(MoveDef m) => MOVES[m.Id] = m;

    /// <summary>Object.assign(MOVES, MIRAGE_MOVES) and the automatic ones (called once by Moves)</summary>
    internal static void Register()
    {
        // ---------------------------------------------------------------- heroes
        // Miyagi (CSBaryonyx, Miracle.usl CPlunder): steals what the target's tech tree "UpdateLimits" allow, up to what
        // its owner has (FightingObj.StealResis). Enemy or neutral animals, buildings, characters, vehicles.
        Add(new MoveDef { Id = "plunder", Target = "enemy", Range = 5, Run = (W, u, t, p, s) =>
        {
            if (!Hostile(u, t) || Array.IndexOf(new[] { "ANML", "BLDG", "CHTR", "VHCL" }, t!.Cls) < 0 || t.Owner == null || u.Owner == null) return false;
            Act(u, "plunder", "attack_front");     // CPlunder: unstoppable while the clip runs
            W.Plunder(u, t);
            return true;
        } });
        // Larry (Hero.usl:491 CallTwins): Barry and Harry come for 60 s at his level and do what he does
        Add(new MoveDef { Id = "call_twins", Self = true, Run = (W, u, t0, p, s) =>
        {
            Act(u, "follow_me", "res_guarding");
            foreach (var (cls, off) in new[] { ("Barry_s0", 4.0), ("Harry_s0", 6.0) })
            {
                double a = u.Heading + W.Rng.Next() * 3.14;
                var c = W.SpawnUnit(cls, u.Owner, u.Pos.X + SMath.Cos(a) * off, u.Pos.Z + SMath.Sin(a) * off, u.Level, u.Heading, new UnitOpts { Temp = true });
                if (c == null) continue;
                W.InheritInfection(u, c);
                W.Later(TWINS_TIME, "vanish", new Args { ["u"] = c });
                var t = u.Task;
                if (t.Type == "build" && t.Building != null) W.Order(c, new Order("build") { Target = t.Building, Auto = true });
                else if (t.Type == "gather" && t.Node != null && t.Node.Alive) W.Order(c, new Order("gather") { Target = t.Node, Auto = true });
                else if (t.Type == "attack" && t.Target != null && t.Target.Alive) W.Order(c, new Order("attack") { Target = t.Target, Auto = true });
            }
            return true;
        } });
        // Stina: hypnotises an enemy animal (the Base CMickDundeeMove: 20 s asleep). fix: MIRAGE's task casts Stina to an
        // animal and ends at once, so the move did nothing.
        Add(new MoveDef { Id = "MickDundee", Target = "animal", Range = 10, Run = (W, u, t, p, s) =>
        {
            if (t == null || t.Cls != "ANML" || !Hostile(u, t)) return false;
            Act(u, "sm_01", "res_sm_jump");
            After(u, 2.6, "hypnosis", new Args { ["t"] = t });
            u.NewAct!.End = Math.Max(u.NewAct.End, u.NewAct.Start + 3.4);
            return true;
        } });
        // Tarna (Hero.usl:1053 -> CMirageReanim): brings back any player's spirit - an enemy's becomes hers. Prays for the
        // spirit's revival time (25 s; 80 % for her own player's spirits) + 1.5 s; the cooldown starts after the revival.
        Add(new MoveDef { Id = "reanimation", Target = "spirit", AnySpirit = true, Range = 25, NoCooldown = true, Run = (W, u, t, p, s) =>
        {
            if (s == null || !s.Alive || s.Busy || u.Owner == null) return false;
            if (u.Owner.SlotFree(s.Level, W.PyramidFor(u.Owner)) != null) return false;
            double D = REANIM_TIME * (s.Owner == u.Owner ? 0.8 : 1.0) + 1.5;
            s.Busy = true;
            u.Owner.QueuedAtLevel[s.Level - 1]++; u.Owner.QueuedUnits++;
            W.Cast(u, "reanimate", D, new Args { ["s"] = s }, "praying_wall", "heal_0");
            return true;
        } });
        // Schliemann (Hero.usl:907 + KleemanDamageBoostStart :923): sacrifices one of his player's or an ally's units: its
        // hit points heal him, then x3 damage, +2000 hp and the zombie look for max(5, hp / 20) s
        Add(new MoveDef { Id = "schliemann_sacrifice", Target = "own", Range = 15, Run = (W, u, t, p, s) =>
        {
            if (t == u || !OwnOrAllied(u, t) || t!.Kind != "unit") return false;
            Act(u, "aje_velo_strike_0");
            After(u, 0.9, "sacrificeBoost", new Args { ["u"] = u, ["t"] = t });
            return true;
        } });
        // Babbage's exo suit (CSpecialMobileSuit). Minigun (CSpecialMinigun): 8 bursts into a 30° cone up to its attack
        // range; each hit 60 % of the victim's current hit points (300..3000) / 8, armour piercing 100 for 15 s.
        Add(new MoveDef { Id = "Babbage_ExoMinigun_0", Target = "enemy", Range = 30, RangeOf = (W, u) => W.AttackRangeOf(u, u.Cs()), Run = (W, u, t, p, s) =>
        {
            if (t != null && !Hostile(u, t)) return false;
            var c = At(t, p);
            W.TimedFilter(u, F.pierce, 15);
            // CSpecialMinigun: 8 bursts while it stands firing (SetSLEAnim "attack_ranged"), unstoppable
            for (int i = 0; i < 8; i++) After(u, i * 0.5, "exoMinigun", new Args { ["u"] = u, ["c"] = c });
            u.NewAct!.End = Math.Max(u.NewAct.End, u.NewAct.Start + 4);
            return true;
        } });
        // jump (CSpecialJumpjet): up to 300 m; the cooldown starts on landing
        Add(new MoveDef { Id = "Babbage_ExoJumpjet_0", Target = "ground", Range = 300, Run = (W, u, t, p, s) => W.JetJump(u, t != null ? t.Pos : p, 300, "jumpjet") });
        Add(new MoveDef { Id = "seas_jumpjet", Target = "ground", Range = 300, Run = (W, u, t, p, s) => W.JetJump(u, t != null ? t.Pos : p, 300, "jumpjet") });
        // ---------------------------------------------------------------- Hu
        // the mammoth's trumpet: the Base move (enemies within 30 m deal x0.8 damage for 15 s)
        Add(new MoveDef { Id = "mammoth_trumpet", Self = true, Run = (W, u, t, p, s) => MOVES["Mammoth_Trumpet"].Run(W, u, t, p, s) });
        // the boar's rage attack (MIRAGE Animal.usl:4480): 2+ enemies close by -> ShakeOff "rage": flat 10 in 8 m. The
        // official tech tree has a Wild_Boar_Rage move too, but no script for it (only the x1.5 damage of FightingObj.usl:
        // 5488, effects.js): with the official game's data nothing happens
        Add(new MoveDef { Id = "Wild_Boar_Rage", Auto = (W, u, e) => W.Data.Extra.Mod == "MIRAGE" && u.Name == "hu_wild_boar" && Crowd(W, u, u.Radius + 5, 2),
            Run = (W, u, t, p, s) => { if (W.Data.Extra.Mod != "MIRAGE") return false; Bash(W, u, "rage", u.Radius + 8, 1.4, 0.1, new[] { 3, 6.9 }, 10, 10); return true; } });
        // jetpack warriors take off and land (Hu.usl:71 JetPackOn / :100 JetPackOff): 3.5 s up to 13 m, 3 s down
        Add(new MoveDef { Id = "jetfly_on", Self = true, NoCooldown = true, Run = (W, u, t, p, s) => W.SetFlying(u, true) });
        Add(new MoveDef { Id = "jetfly_off", Self = true, NoCooldown = true, Run = (W, u, t, p, s) => W.SetFlying(u, false) });
        // Tesla's level 4/5 lets the Norse infantry leap (the Base jetpack: up to 100 m)
        Add(new MoveDef { Id = "jetleap", Target = "ground", Range = 100, Run = (W, u, t, p, s) => !u.Flying && W.JetJump(u, t != null ? t.Pos : p, 100, "sm_jump_01", "jump") });
        // the druid's meteor (CMeteor / CMeteorite): 7 s of prayer within 70 m, then a rock falls for 3 s and hits every
        // owner's objects within 30 m - 250, armour piercing 100, size class 10 throws. The cooldown starts with the fall.
        Add(new MoveDef { Id = "meteor", Target = "ground", Range = 70, NoCooldown = true, Run = (W, u, t, p, s) =>
        {
            var q = At(t, p);
            W.Cast(u, "meteor", 7, new Args { ["p"] = q }, "NS2010_poor_beg", "heal_0");
            return true;
        } });
        // ---------------------------------------------------------------- Aje
        // the brachiosaurus charges (Animal.usl:2925 BrachioRageTo): every 3.5 s, 9 m area damage (its weapon, falling off
        // to a fifth) and characters within 7 m pushed aside, until it attacks (at most 60 s); the 30 s cooldown follows
        Add(new MoveDef { Id = "BrachioStampede", NoCooldown = true, Chase = true,
            Auto = (W, u, e) => u.Name == "aje_brachiosaurus" && u.RageT == 0 && e != null && W.Reach(u, e) >= 10 && W.Reach(u, e) <= u.Fow,
            Run = (W, u, e, p, s) => { u.RageT = 60; W.PeriodicEffect(u, 60, 3.5, "brachioRage", new Args { ["e"] = e }, true); return true; } });
        // the shaman's blinding storm (CBlindingStorm / CSandStorm): a 20 m dust cloud for 15 s; inside it every owner's
        // units take no projectile, direct or tower damage and fight at melee range only
        Add(new MoveDef { Id = "blinding_storm", Target = "ground", Range = 21, Run = (W, u, t, p, s) =>
        {
            Act(u, "thanking", "heal_0");
            W.SpawnStorm(At(t, p));
            return true;
        } });
        // ---------------------------------------------------------------- Ninigi
        // the triceratops' frightening (CFrightening): enemies within 30 m attack x1.5 slower for 15 s
        Add(new MoveDef { Id = "frightening", Self = true, Run = (W, u, t, p, s) =>
        {
            Act(u, "sm_shake_off");
            foreach (var v in EnemiesNear(W, u, u.Pos, 30, buildings: true)) W.TimedFilter(v, F.fright, 15);
            return true;
        } });
        // the drums' song of valor (CSongOfValor): own and allied units within 30 m attack x0.9 faster for 10 s
        Add(new MoveDef { Id = "song_of_valor", Self = true, Run = (W, u, t, p, s) =>
        {
            Act(u, "menace");
            foreach (var v in FriendsNear(W, u, u.Pos, 30, AnCh)) W.TimedFilter(v, F.valor, 10);
            return true;
        } });
        // the monk's peace gong (CPeaceGong): 3 s, then an own or allied unit or building can't be hurt, attacked or fight
        // for 10 s. fix: MIRAGE computes but doesn't check that the target is a friend.
        Add(new MoveDef { Id = "ceasefire", Target = "own", Range = 15, NoCooldown = true, Run = (W, u, t, p, s) =>
        {
            if (!OwnOrAllied(u, t)) return false;
            W.Cast(u, "ceasefire", 3, new Args { ["t"] = t }, "affirming_intense", "heal_0");
            return true;
        } });
        // the monk's curse (CDeplenish): enemy transporters within 30 m drop their passengers and can't load for 15 s
        // (not those on deep water)
        Add(new MoveDef { Id = "deplenish", Self = true, Run = (W, u, t, p, s) =>
        {
            Act(u, "heal_0");
            foreach (var v in EnemiesNear(W, u, u.Pos, 30))
            {
                var pax = W.PassengersOf(v);
                if (pax == null && W.Capacity(v) == 0) continue;
                if (W.DeepWaterAt(v.Pos.X, v.Pos.Z)) continue;
                if (pax != null && pax.Count > 0) W.UnloadAll(v, false);
                v.St.T["cursed"] = W.Time + 15;
            }
            W.Sfx("FX_CUnboardCurse", u.Pos);
            return true;
        } });
        // ---------------------------------------------------------------- SEAS
        // the cyborg rex roars (CCyborgRoar): own and allied units within 30 m (itself too) deal x1.5 damage for 15 s
        Add(new MoveDef { Id = "cyborg_roar", Self = true, Run = (W, u, t, p, s) => { Act(u, "roaring", "menace"); After(u, 0.8, "cyborgRoar", new Args { ["u"] = u }); return true; } });
        // the rex's dynamite (ShakeOff "shake"): enemies within its radius + 20 m - characters thrown, 100 flat damage
        Add(new MoveDef { Id = "cyborg_tnt", Self = true, Run = (W, u, t, p, s) => { Bash(W, u, new[] { "shake", "sm_shake_off" }, u.Radius + 20, 0.1, 0.2, new[] { 3, 6.9 }, 100, 100); return true; } });
        Add(new MoveDef { Id = "triceratops_shake_off", Self = true, Run = (W, u, t, p, s) => { Bash(W, u, "sm_shake_off", u.Radius + 8, 0.4, 2.0, new[] { 3, 6.9 }, 50, 50); return true; } });
        // the triceratops' tusk (CTriceratopsHook): its enemy in front and in reach takes x3 damage, ignoring armour
        Add(new MoveDef { Id = "triceratops_tusk", Self = true, Run = (W, u, t, p, s) =>
        {
            var e = u.Task.Target ?? u.LastTarget;
            if (e == null || !e.Alive || !InFront(u, e) || W.RangeZone(u, e) == 0) return false;
            Act(u, "pawing");
            W.TakeDmg(u, e, 3.0, 0, null, 100);
            if (e.Cls == "CHTR") W.Knockback(e, u.Pos, 10, 0.7);
            return true;
        } });
        // stimpack (CSEAS.Stimpack): -40 hit points, x0.75 attack time for 10 s (not the medic, not below 41 hp). The
        // carriers (CTransportObj.Stimpack) stim their ranged SEAS passengers. fix: the 5 s cooldown of the tech tree is
        // applied (MIRAGE never starts it).
        Add(new MoveDef { Id = "stimpack", Self = true, Run = (W, u, t, p, s) =>
        {
            if (u.Cls != "CHTR") { int n = 0; foreach (var q in u.Passengers ?? new List<Unit>()) if (W.Stim(q, true)) n++; return n > 0; }
            return W.Stim(u);
        } });
        Add(new MoveDef { Id = "stimpack_rex", Self = true, Run = (W, u, t, p, s) => MOVES["stimpack"].Run(W, u, t, p, s) });
        // the medic's blood infection (CHemoplague): an enemy character or animal can't be healed for 20 s, poison x2
        Add(new MoveDef { Id = "contamination", Target = "enemy", Range = 10, Run = (W, u, t, p, s) =>
        {
            if (!Hostile(u, t) || (t!.Cls != "CHTR" && t.Cls != "ANML")) return false;
            Act(u, "heal_0");
            t.St.T["noHeal"] = W.Time + 20;
            return true;
        } });
        // the medic's deafening (DeafeningRoar): enemy units and buildings within 15 m take no orders for 15 s.
        // fix: queued with Shift it had radius 0.
        Add(new MoveDef { Id = "deafening", Self = true, Run = (W, u, t, p, s) =>
        {
            foreach (var v in EnemiesNear(W, u, u.Pos, 15, buildings: true)) v.St.T["deaf"] = W.Time + 15;
            return true;
        } });
        // the medic's parasite (CParasites / CParasite): her player sees what an enemy unit sees. fix: it lasts 480 s
        // (MIRAGE checks the wrong timer and it never ends).
        Add(new MoveDef { Id = "parasite", Target = "enemy", Range = 30, Run = (W, u, t, p, s) =>
        {
            if (!Hostile(u, t) || Array.IndexOf(new[] { "ANML", "CHTR", "SHIP", "VHCL" }, t!.Cls) < 0) return false;
            Act(u, "tec_sm_cocktail", "heal_0");
            W.Watchers.Add(new Watcher { Player = u.Owner, Host = t, Until = W.Time + 480 });
            W.Sfx("FX_CParasite", u.Pos);
            return true;
        } });
        // the medic's walking bomb (CWalkingBomb / DarkRitual / MakeThemPay): one of her player's units explodes after
        // 10 s - its radius + 30 m, 45 % of its max hit points falling off to 25 %, every owner's objects, size 10 throws
        Add(new MoveDef { Id = "suicide", Target = "own", Range = 3, Run = (W, u, t, p, s) =>
        {
            if (t == null || t == u || t.Owner != u.Owner || t.Kind != "unit") return false;
            Act(u, "tec_sm_cocktail", "heal_0");
            After(u, 1.0, "walkingBomb", new Args { ["t"] = t });
            return true;
        } });
        // the exo suit's automatic moves (CExoBash / CExoKick; their inventions are made from the start)
        Add(new MoveDef { Id = "bash_exo", Auto = (W, u, e) => u.Name == "seas_mobile_suit" && Crowd(W, u, u.Radius + 5, 3),
            Run = (W, u, t, p, s) => { double d = u.Cs() is { } cs ? cs.Dmg : 0; Bash(W, u, "seas_mobile_suit_sm_0", u.Radius + 5, 1.2, 0.1, new[] { 1, 3.9 }, 1.5 * d, 1.25 * d); return true; } });
        Add(new MoveDef { Id = "kick_down", Auto = (W, u, e) => u.Name == "seas_mobile_suit" && e is Unit && InFront(u, e) && W.RangeZone(u, e) > 0,
            Run = (W, u, e, p, s) => { Act(u, "seas_mobile_suit_fm_0"); W.TakeDmg(u, e!, 2.0, 0); if (e!.Cls == "CHTR") W.Knockback(e, u.Pos, 10, 0.7); return true; } });
        // the exocutor's hand (CMKHand): enemy characters and animals within 10 m thrown (size class 10), no damage
        Add(new MoveDef { Id = "exocutor_hand", Self = true, Run = (W, u, t, p, s) =>
        {
            Act(u, "lumberjack_sm_0");
            foreach (var v in EnemiesNear(W, u, u.Pos, 10)) W.ThrowUnit(v, u.Pos, 10);
            return true;
        } });
        // the minigun lumberjack's explosive rounds (ActivateExplosiveRounds): a switch. While on: better guns, no armour,
        // 15 damage to itself and 100..50 to enemies within 10 m every second. The cooldown starts when switched off.
        Add(new MoveDef { Id = "explosives_rounds", Self = true, NoCooldown = true, Run = (W, u, t, p, s) => W.ExplosiveRounds(u, !u.Rounds) });
        // any animal of the player: the clicked unit mounts it (FightingObj.MountThatUnit -> BoardTransport)
        Add(new MoveDef { Id = "MountThatUnit", Target = "own", Range = 1e9, NoCooldown = true, Run = (W, u, t, p, s) =>
        {
            if (t is not Unit tu || tu.Owner != u.Owner || tu == u || !W.CanBoard(tu, u)) return false;
            W.Order(tu, new Order("board") { Target = u });
            return true;
        } });

        AUTO_ORDER.AddRange(new[] { "Wild_Boar_Rage", "BrachioStampede", "bash_exo", "kick_down" });
        AUTO_INVENT["Wild_Boar_Rage"] = "wild_boar_rage"; AUTO_INVENT["BrachioStampede"] = "brachiostampede";
        AUTO_INVENT["bash_exo"] = "exo_bash"; AUTO_INVENT["kick_down"] = "exo_kick";
        // the flyers' "Land" and "Take off" (MIRAGE flying units v3/v4: Systems/Flyers.cs)
        Flyers.Register(MOVES);
    }

    // ------------------------------------------------------------------ cast results (World.Cast)
    internal static void CastDone(World W, Unit u, string kind, Args a)
    {
        switch (kind)
        {
            case "reanimate":
            {
                var s = a.Get<Spirit>("s")!;
                u.Owner!.QueuedAtLevel[s.Level - 1]--; u.Owner.QueuedUnits--;
                s.Busy = false;
                if (!s.Alive) return;
                s.Alive = false;
                var r = W.SpawnUnit(s.Name, u.Owner, s.Pos.X, s.Pos.Z, s.Level);
                if (r != null) W.Emit("trained", new Args { ["unit"] = r, ["producer"] = u });
                u.Cd.Set("reanimation", W.Time + W.MoveTime(u, "reanimation"));
                return;
            }
            case "meteor":
            {
                var p = a.V("p");
                u.Cd.Set("meteor", W.Time + W.MoveTime(u, "meteor"));
                // the rock (CMeteorite): 3 s, then it falls from 65 m above the druid onto the point
                W.Later(3, "meteorFall", new Args { ["p"] = p, ["from"] = new V3(u.Pos.X, u.Pos.Y + 65, u.Pos.Z), ["by"] = u.Owner, ["u"] = u });
                return;
            }
            case "ceasefire":
            {
                var t = a.E("t")!;
                u.Cd.Set("ceasefire", W.Time + W.MoveTime(u, "ceasefire"));
                if (!t.Alive) return;
                t.St.T["peace"] = W.Time + 10;
                if (t is Unit tu && tu.Task.Type == "attack") tu.Task = UnitTask.Idle();
                W.Emit("healfx", new Args { ["to"] = t });
                W.Sfx("FX_CPeace_Gong", u.Pos);
                return;
            }
        }
        throw new ArgumentException("unknown cast " + kind);
    }
}

// ------------------------------------------------------------------------------------------------ world mixin
public sealed partial class World
{
    /// <summary>a unit-local filter for `dur` seconds; casting it again restarts the time (SetFrighteningEffect & co.)</summary>
    public void TimedFilter(Entity e, string path, double dur)
    {
        if (!e.Alive || Data.Base.Filter(path) == null) return;
        var k = "f:" + path;
        e.St.T[k] = Time + dur;
        if (!(e.LocalTT != null && e.LocalTT.Has(path))) LocalFilter(e, path, true);
        Later(dur, "filterEnd", new Args { ["e"] = e, ["path"] = path });
    }

    public void Sfx(string name, V3 pos) => Emit("sfx", new Args { ["name"] = name, ["pos"] = pos });

    /// <summary>a cast that stands still for `sec` seconds (a new order breaks it): then the cast result of `kind`</summary>
    public void Cast(Unit u, string kind, double sec, Args args, params string[] anims)
    {
        ReleaseTask(u);
        u.Task = new UnitTask("cast") { CastKind = kind, Until = Time + sec, CastArgs = args };
        u.Path.Clear(); u.Vel = V3.Zero;
        var a = anims.Length > 0 ? u.Anim!.Pick(anims) : null;
        if (a != null) { u.BusyAnim = true; u.Anim!.Play(a, new AnimOpts { Fade = 0.2 }); }
    }

    /// <summary>a new order or death broke the cast</summary>
    partial void CastCancel(Unit u)
    {
        var t = u.Task;
        u.BusyAnim = false;
        if (t.CastKind == "reanimate") { var s = t.CastArgs!.Get<Spirit>("s")!; u.Owner!.QueuedAtLevel[s.Level - 1]--; u.Owner.QueuedUnits--; s.Busy = false; }
    }

    partial void CastUpdate(Unit u, double dt)
    {
        var t = u.Task;
        u.Vel = V3.Zero;
        if (Time < t.Until) return;
        u.BusyAnim = false;
        u.Task = UnitTask.Idle();
        MirageMoves.CastDone(this, u, t.CastKind!, t.CastArgs!);
    }

    /// <summary>a jetpack jump (Jetpack / CJumpjet / CSpecialJumpjet): the cooldown starts on landing</summary>
    public bool JetJump(Unit u, V3? pos, double max, params string[] anim)
    {
        if (pos == null || u.Flying) return false;
        Jump(u, pos.Value, max);
        if (anim.Length > 0) Moves.Play(u, anim);
        return true;
    }

    /// <summary>Miyagi's plunder (FightingObj.StealResis): the target's "UpdateLimits", at most what its owner has</summary>
    public void Plunder(Unit u, Entity t)
    {
        var o = Data.ObjectNode(t.Name, t.Owner); var lim = o?["UpdateLimits"];
        if (lim == null) return;
        var got = new List<(string R, double X)>();
        foreach (var r in new[] { "food", "wood", "stone" })
        {
            double max = TT.Num(lim["max_" + r]?.Scalar, 0);
            double room = Math.Max(0, (u.Owner!.Caps.TryGetValue(r, out var cap) ? cap : 1e9) - u.Owner.Res[r]);
            double x = Math.Floor(Math.Max(0, Math.Min(Math.Min(max, t.Owner!.Res[r]), room)));
            if (x == 0) continue;
            t.Owner.Res[r] -= x; u.Owner.Res[r] += x; got.Add((r, x));
        }
        var txt = string.Join(", ", got.Select(g => $"{TT.JsString(g.X)} {g.R}"));
        if (txt == "") return;
        Emit("msg", new Args { ["player"] = t.Owner, ["text"] = $"Resources stolen: {txt}", ["kind"] = "bad" });
        Emit("msg", new Args { ["player"] = u.Owner, ["text"] = $"Plundered {txt}" });
        Emit("attacked", new Args { ["entity"] = t, ["by"] = u });
    }

    /// <summary>jetpack flight of the jetpack warrior (Hu.usl JetPackOn / JetPackOff)</summary>
    public bool SetFlying(Unit u, bool on)
    {
        if (u.FlyChange > Time || u.Flying == on || u.Inside) return false;
        if (on)
        {
            ReleaseTask(u); u.Task = UnitTask.Idle(); u.Path.Clear();
            LocalFilter(u, MirageMoves.F.jet, true);
            u.Flying = true;
            u.FlyChange = Time + 3.5;
            u.Lift = new Lift { From = u.LiftH, To = MirageMoves.FLY_HEIGHT, T = 0, TT = 3.5 };
        }
        else
        {
            if (DeepWaterAt(u.Pos.X, u.Pos.Z)) return false;     // not over water (Hu.usl:180)
            ReleaseTask(u); u.Task = UnitTask.Idle(); u.Path.Clear();
            u.FlyChange = Time + 3.0;
            u.Lift = new Lift { From = u.LiftH, To = 0, T = 0, TT = 3.0 };
            Later(3.0, "landed", new Args { ["u"] = u });
        }
        Moves.Play(u, "sm_jump_01", "jump");
        return true;
    }

    /// <summary>the minigun lumberjack's explosive rounds</summary>
    public bool ExplosiveRounds(Unit u, bool on)
    {
        if (on)
        {
            if (u.Cd.Get("explosives_rounds") > Time) return false;
            u.Rounds = true;
            LocalFilter(u, MirageMoves.F.rounds, true);
            PeriodicEffect(u, 1e9, 1, "rounds");
            Emit("fx", new Args { ["entity"] = u, ["gfx"] = "fire_molotov", ["until"] = 1e12, ["key"] = "rounds", ["link"] = "FIRE" });
        }
        else
        {
            u.Rounds = false;
            LocalFilter(u, MirageMoves.F.rounds, false);
            u.Cd.Set("explosives_rounds", Time + 60);
            Emit("fx", new Args { ["entity"] = u, ["key"] = "rounds", ["until"] = 0.0 });
        }
        return true;
    }

    public bool Stim(Unit u, bool carried = false)
    {
        if (u.Cls != "CHTR" || u.Name == "seas_medic" || u.St.Active("stim", Time) || u.Hp <= 40) return false;
        if (carried && !((u.Weapons.Long != null && u.Weapons.Long.Projectile != null) || u.Name == "seas_flamethrower")) return false;
        if (!u.Name.StartsWith("seas_", StringComparison.Ordinal) && Data.Info(u.Name)?.Tribe != "SEAS") return false;
        u.Hp -= 40;
        u.St.T["stim"] = Time + 10;
        TimedFilter(u, MirageMoves.F.stim, 10);
        return true;
    }

    /// <summary>the blinding storm's cloud (CSandStorm): 20 m, 15 s (the effect's state: pos, R, snd)</summary>
    public void SpawnStorm(V3 p)
    {
        Emit("fxAt", new Args { ["pos"] = p, ["gfx"] = "fx_dust_twister", ["until"] = Time + 15, ["loop"] = true });
        PeriodicEffect(null, 15, 0.25, "storm", new Args { ["pos"] = p, ["R"] = 20.0, ["snd"] = 0.0 }, true);
    }

    /// <summary>deep water (terrain more than 5 m under the sea level): no landing, no curse, Babbage drowns</summary>
    public bool DeepWaterAt(double x, double z) => WaterLevel != null && Height(x, z) + 5 < WaterLevel.Value;

    /// <summary>the exo suit is destroyed (CSpecialMobileSuit.Die / TIMER_BABBAGE): after its dying animation Babbage climbs
    /// out with half his hit points and the minigun's cooldown - in deep water he drowns (combat.js kill calls it)</summary>
    public void SuitDestroyed(Unit s) { if (s.Driver != null && s.Owner != null) Later(2.5, "driverOut", new Args { ["s"] = s }); }

    /// <summary>Babbage's mechanization: 12 s up into the sky, then the exo suit comes down where he was (Hero.usl:1169,
    /// TransformationServo :1191; Mirage.usl CSpecialMobileSuit.Descent :1028)</summary>
    public void Mechanize(Unit b)
    {
        if (b.SuitT != 0 || !b.Alive) return;
        ReleaseTask(b); b.Task = UnitTask.Idle(); b.Path.Clear();
        b.SuitT = Time + 12;
        b.Untargetable = true; b.Unselectable = true;
        b.St.T["invul"] = Time + 12;
        Trap(b, 12, "mechanize");
        b.Lift = new Lift { From = 0, To = 150, T = 0, TT = 12 };
        Moves.Play(b, "jetpack", "sm_jump_01");
        Emit("removed", new Args { ["entity"] = b });           // out of the selection and the groups
        Later(12, "suitDown", new Args { ["b"] = b });
    }
}

static class MirageDelayed
{
    [Delayed("filterEnd")]
    static void FilterEnd(World W, Args a)
    {
        var e = a.E("e")!; var path = a.Str("path")!;
        if (e.Alive && (e.St.T.TryGetValue("f:" + path, out var until) ? until : 0) <= W.Time + 1e-6 && e.LocalTT != null && e.LocalTT.Has(path)) W.LocalFilter(e, path, false);
    }

    [Delayed("vanish")]
    static void Vanish(World W, Args a) { var u = a.U("u")!; if (u.Alive) W.RemoveEntity(u); }

    [Delayed("sacrificeBoost")]
    static void SacrificeBoost(World W, Args a)
    {
        var u = a.U("u")!; var t = a.E("t")!;
        if (!u.Alive || !t.Alive) return;
        double hp = t.Hp;
        t.SilentDeath = false;
        W.Kill(t, null);
        u.Hp = Math.Min(u.MaxHp, u.Hp + hp);
        W.TimedFilter(u, MirageMoves.F.sacrifice, Math.Max(5, hp / 20));
    }

    [Delayed("cyborgRoar")]
    static void CyborgRoar(World W, Args a)
    {
        var u = a.U("u")!;
        if (!u.Alive) return;
        foreach (var v in MirageMoves.FriendsNear(W, u, u.Pos, 30, new[] { "ANML", "CHTR", "VHCL" })) v.St.T["cyborg"] = W.Time + 15;
    }

    [Delayed("walkingBomb")]
    static void WalkingBomb(World W, Args a)
    {
        var t = a.E("t")!;
        if (!t.Alive) return;
        t.St.T["bomb"] = W.Time + 10;
        W.Later(10, "bombBlast", new Args { ["t"] = t });
    }

    [Delayed("bombBlast")]
    static void BombBlast(World W, Args a)
    {
        var t = a.E("t")!;
        if (!t.Alive) return;
        var p = t.Pos;
        W.AreaDamage(null, p, t.Radius + 30, 0.45 * t.MaxHp, 0.25 * t.MaxHp, new AreaOpts { Direct = true, All = true, Size = 10, Owner = t.Owner, Skip = t });
        W.Emit("blast", new Args { ["pos"] = p, ["size"] = "big" });
        t.SilentDeath = true;
        W.RemoveEntity(t);
    }

    [Delayed("meteorFall")]
    static void MeteorFall(World W, Args a)
    {
        var p = a.V("p");
        W.Emit("fxFly", new Args { ["gfx"] = "rolling_stone", ["from"] = a.V("from"), ["to"] = p, ["T"] = 1.2 });
        W.Later(1.2, "meteorHit", new Args { ["p"] = p, ["u"] = a.U("u") });
    }

    [Delayed("meteorHit")]
    static void MeteorHit(World W, Args a)
    {
        var p = a.V("p"); var u = a.U("u")!;
        W.AreaDamage(null, p, 30, 250, 250, new AreaOpts { Direct = true, All = true, Ap = 100, Size = 10, Owner = u.Owner });
        W.Emit("blast", new Args { ["pos"] = p, ["size"] = "big" });
        W.Sfx("FX_CMeteorite", p);
    }

    [Delayed("landed")]
    static void Landed(World W, Args a)
    {
        var u = a.U("u")!;
        if (!u.Alive) return;
        u.Flying = false;
        W.LocalFilter(u, MirageMoves.F.jet, false);
        var nav = W.NavFor(u);
        if (!nav.IsFree(u.Pos.X, u.Pos.Z)) { int c = nav.NearestFree(nav.Idx(u.Pos.X, u.Pos.Z), 8); (u.Pos.X, u.Pos.Z) = nav.Center(c); }
        u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z;
    }

    [Delayed("driverOut")]
    static void DriverOut(World W, Args a)
    {
        var s = a.U("s")!;
        if (W.DeepWaterAt(s.Pos.X, s.Pos.Z) || s.Owner == null) return;
        var b = W.SpawnUnit(s.Driver!, s.Owner, s.Pos.X, s.Pos.Z, s.Level, s.Heading);
        if (b == null) return;
        W.InheritInfection(s, b);
        b.Hp = b.MaxHp / 2;
        double cd = s.Cd.Get("Babbage_ExoMinigun_0");
        if (cd != 0) b.Cd.Set("Babbage_Minigun_0", cd);
        Moves.Play(b, "getting_up");
        W.Emit("trained", new Args { ["unit"] = b, ["producer"] = null });
    }

    // the exo suit lands where Babbage went up (it keeps his level, his kills and his minigun's cooldown)
    [Delayed("suitDown")]
    static void SuitDown(World W, Args a)
    {
        var b = a.U("b")!;
        if (!b.Alive) return;
        var p = b.Owner;
        double cd = b.Cd.Get("Babbage_Minigun_0");
        b.SilentDeath = true;
        W.RemoveEntity(b);
        var s = W.SpawnUnit("special_mobile_suit", p, b.Pos.X, b.Pos.Z, b.Level, b.Heading);
        if (s == null) { W.InheritInfection(b, W.SpawnUnit("babbage_s0", p, b.Pos.X, b.Pos.Z, b.Level, b.Heading)); return; }
        W.InheritInfection(b, s);
        s.Driver = "babbage_s0";
        if (cd != 0) s.Cd.Set("Babbage_ExoMinigun_0", cd);
        s.LiftH = 150; s.Lift = new Lift { From = 150, To = 0, T = 0, TT = 10 };
        s.St.T["invul"] = W.Time + 10;
        W.Trap(s, 10, "descent");
        Moves.Play(s, "jumpjet");
        W.Emit("trained", new Args { ["unit"] = s, ["producer"] = null });
    }

    // ------------------------------------------------------------------ periodic
    // a burst of the exo suit's minigun (8 of them, every 0.5 s: Babbage_ExoMinigun_0)
    [Delayed("exoMinigun")]
    static void ExoMinigun(World W, Args a)
    {
        var u = a.U("u")!;
        if (!u.Alive) return;
        var clip = u.Anim!.Pick("attack_ranged", "attack_front");
        if (clip != null) u.Anim.Play(clip, new AnimOpts { Loop = false, Restart = true, Fade = 0.1 });
        double R = W.AttackRangeOf(u, u.Cs()) + u.Radius;
        var c = a.V("c");
        double dir = SMath.Atan2(c.Z - u.Pos.Z, c.X - u.Pos.X);
        foreach (var v in Moves.EnemiesNear(W, u, u.Pos, R, buildings: true))
        {
            double an = SMath.Atan2(v.Pos.Z - u.Pos.Z, v.Pos.X - u.Pos.X) - dir;
            if (Math.Abs(SMath.Atan2(SMath.Sin(an), SMath.Cos(an))) <= Math.PI / 12) W.TakeDirectDmg(v, Moves.Pct(v, 0.6, 300, 3000) * 0.125, 100, u.Owner, true, u);
        }
    }

    // it ends when the brachiosaurus attacks or has nobody to charge at (EndRageTo on AttackEnemy / OnActionEnd)
    [Periodic("brachioRage")]
    static bool BrachioRage(World W, PeriodicEffect f)
    {
        var u = (Unit)f.Host!;
        var e = u.Task.Type == "attack" ? u.Task.Target : null;
        if (!u.Alive || e == null || !e.Alive || W.RangeZone(u, e) > 0) return false;
        double d = u.Cs() is { } cs ? cs.Dmg : 0;
        W.AreaDamage(u, u.Pos, 9, d, d / 5, new AreaOpts { Direct = true, Size = u.Stats!.Size });
        foreach (var v in Moves.EnemiesNear(W, u, u.Pos, 7, types: new[] { "CHTR" })) W.Knockback(v, u.Pos, 3, 0);
        return true;
    }
    [PeriodicEnd("brachioRage")]
    static void BrachioRageEnd(World W, PeriodicEffect f) { var u = (Unit)f.Host!; u.RageT = 0; u.Cd.Set("BrachioStampede", W.Time + W.MoveTime(u, "BrachioStampede")); }

    [Periodic("storm")]
    static bool Storm(World W, PeriodicEffect f)
    {
        var s = f.S; var p = s.V("pos"); double R = s.Num("R");
        W.UHash.Query(p.X, p.Z, R + 8, v =>
        {
            if (v.Alive && !v.Inside && Math.Sqrt((v.Pos.X - p.X) * (v.Pos.X - p.X) + (v.Pos.Z - p.Z) * (v.Pos.Z - p.Z)) - v.Radius < R) v.St.T["storm"] = W.Time + 0.3;
        });
        double snd = s.Num("snd") - 0.25;
        if (snd <= 0) { snd = 6; W.Sfx("FX_CBlinding_Storm", p); }
        s["snd"] = snd;
        return true;
    }

    [Periodic("rounds")]
    static bool Rounds(World W, PeriodicEffect f)
    {
        var u = (Unit)f.Host!;
        if (!u.Alive || !u.Rounds) return false;
        W.Damage(u, 15, null);
        if (u.Alive) W.AreaDamage(u, u.Pos, 10, 100, 50, new AreaOpts { Direct = true });
        return true;
    }
}
