// What an owned animal wears and when a weapon's fire sprays (docs/VIEW.md "Animal looks", docs/EFFECTS.md):
//  * an arena animal (CArenaAnimal) keeps the wild look under an owner - mission 10's arena_allosaurus of player 5
//    ("Warden's Pet") wore the tamed allosaurus' saddle and harness; a tribe's own allosaurus wears them
//  * the weapons' attack clips of the rules (Rules.WeaponClips): the SEAS flamethrower fires with "seas_flamethrower_0",
//    not an "attack" clip - its flame stream and smoke spray only while one plays (the view: Particles.IsAttackClip)
//  * a dead carrier's parts stop (Composite.Died): the ankylosaurus' catapult kept swinging "attack_front" on the
//    dead body - riders, turrets, build-ups hold a pose, no clip of theirs runs on
using System.Linq;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class AnimalLookTests
{
    const int SADDLE = 6, PARTYCOL = 5;

    public static void ArenaAnimalKeepsWildLook(TestContext t)
    {
        var (R, M) = Sys1.Load(t, t.Data("PW_DATA"));
        var W = Sys1.NewWorld(R, M);
        var p = Sys1.AddPlayer(W, R, 1, "Aje");
        var arena = W.SpawnUnit("arena_allosaurus", p, 100, 100)!;
        var tamed = W.SpawnUnit("aje_allosaurus", p, 140, 100)!;
        t.True(Compose.WildLook(R, "arena_allosaurus") && !Compose.WildLook(R, "aje_allosaurus"), "arena_allosaurus is an arena animal, aje_allosaurus not");
        t.True(((arena.HideMask >> SADDLE) & 1) == 1 && ((arena.HideMask >> PARTYCOL) & 1) == 1 && !arena.PartyTinted,
            $"an owned arena allosaurus: saddle and party colour hidden (mask {arena.HideMask:x})");
        t.True(((tamed.HideMask >> SADDLE) & 1) == 0 && tamed.PartyTinted, $"an owned aje_allosaurus: saddle shown (mask {tamed.HideMask:x})");
    }

    public static void WeaponClipsOfTheRules(TestContext t)
    {
        var (R, _) = Sys1.Load(t, t.Data("PW_DATA"));
        var c = R.WeaponClips();
        t.True(c.Contains("seas_flamethrower_0") && c.Contains("seas_gunner_0") && c.Contains("marksman_gatling"), "the SEAS weapons' clips");
        t.True(!c.Contains("standanim"), "no standing clip");
    }

    public static void DeadCarrierPartsHold(TestContext t)
    {
        var (R, M) = Sys1.Load(t, t.Data("PW_DATA"));
        var W = Sys1.NewWorld(R, M);
        var hu = Sys1.AddPlayer(W, R, 1, "Hu", 1);
        var aje = Sys1.AddPlayer(W, R, 2, "Aje", 2);
        var titan = W.SpawnUnit("hu_triceratops", hu, 100, 100)!;
        t.True(titan.Comp != null && titan.Comp.Parts.Any(p => p.Anim != null), $"the titan's parts ({titan.Comp?.Parts.Count})");
        for (int i = 0; i < 4; i++) W.SpawnUnit("aje_warrior", aje, 110 + i * 3, 112);
        // fight until a gunner's clip runs
        for (int i = 0; i < 400 && !titan.Comp!.Parts.Any(p => p.Spec!.Kind == "turret" && p.Anim?.Cur?.IsRunning == true); i++) W.Step();
        t.True(titan.Comp!.Parts.Any(p => p.Anim?.Cur?.IsRunning == true), "a part's clip runs while the titan lives and fights");
        W.Kill(titan, null);
        var before = titan.Comp.Parts.Where(p => p.Anim?.Cur != null).Select(p => (p, p.Anim!.Cur!.Clip, p.Anim.Cur.Time)).ToList();
        for (int i = 0; i < 60; i++) W.Step();
        foreach (var (p, clip, time) in before)
            t.True(!p.Anim!.Cur!.IsRunning && p.Anim.Cur.Clip == clip && p.Anim.Cur.Time == time,
                $"dead titan: {p.Spec!.Kind} {p.Gfx} holds {p.Anim.Cur} (was {clip} {time:0.00})");
        t.True(before.Count > 0, $"parts checked ({before.Count})");
    }
}
