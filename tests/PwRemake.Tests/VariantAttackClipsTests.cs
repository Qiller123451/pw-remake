// Which clips do the variants of a unit play when they attack? Many units share a base model and differ in their
// add-ons (riders, howdahs, catapults, launchers, cannons, turrets) and in their weapons: MIRAGE's saltasaurus launcher
// is the saltasaurus of the archer variant with a launcher on its back and only a ranged weapon, whose table copies the
// animal's strike "attack_front" - it stomped while the launcher fired. The rule (Compose.CarrierStrikes / GunSpec,
// Combat.AttackUpdate): a ranged shot fired by a gun add-on - the animal stands, the gun plays its clip, the crew its
// attack pose; a melee blow is the carrier's strike and the gun stays still.
//
// VariantAttackClips lists, for every class with add-ons and every weapon it has or gains / loses by an upgrade (the
// Filters' Users appends and removes: the catapult ankylosaurus), the clips the carrier, the gun and the rider play,
// and fails on a melee strike of an animal for a shot its gun fires, a gun firing for a melee blow, or a melee blow
// without the carrier's strike. RhinoBallistaStands plays it in a world with the base models (the woolly rhino's clips).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class VariantAttackClipsTests
{
    public static void VariantAttackClipsBase(TestContext t) => Check(t, RulesTestData.Base(t));
    public static void VariantAttackClipsMirage(TestContext t) => Check(t, RulesTestData.Mirage(t));

    static void Check(TestContext t, string data)
    {
        var (R, M) = Sys1.LoadBare(t, data);
        var tt = Json.ParseFile(System.IO.Path.Combine(data, "techtree.json"))!;
        // the Filters that move a class to other weapons: filter path -> (class, weapon, added?)
        var moves = new List<(string Filter, string Cls, string Weapon, bool Add)>();
        void Walk(JsonNode? n, string path)
        {
            if (n is not JsonObject o) return;
            if (o["op"]?.ToString() is string op && o["path"]?.ToString() is string p
                && System.Text.RegularExpressions.Regex.Match(p, @"^/Objects/\w+/Weapons/(\w+)/Users") is { Success: true } m)
            {
                var f = path.Substring(0, Math.Max(0, path.LastIndexOf("/Modificators/", StringComparison.Ordinal)));
                if (op == "append" && o["value"]?.ToString() is string v) moves.Add((f, v, m.Groups[1].Value, true));
                else if (op == "remove") moves.Add((f, "", m.Groups[1].Value, false));
                return;
            }
            foreach (var (k, v) in o) Walk(v, path + "/" + k);
        }
        Walk(tt["Filters"], "");
        var bad = new List<string>(); var known = new List<string>();
        int rows = 0, classes = 0;
        foreach (var c in R.IndexOrder)
        {
            var specs = R.Composites(c).Where(s => s.Cond != "construction").ToList();
            if (!specs.Any(s => s.Kind is "turret" or "buildup" or "rider")) continue;
            var info = R.Info(c);
            if (info == null || info.Type == "BLDG") continue;
            bool animal = Compose.IsAnimal(info.Type, M.Get(R.ClassGfx(c) ?? "")?.Arch);
            var start = R.WeaponPaths(c, null).Select(p => p.Substring(p.LastIndexOf('/') + 1)).ToList();
            // variants: the start tree, and each filter that moves the class's weapons (upgrades: the catapult)
            var variants = new List<(string Name, List<string> Weapons, string? Filter)> { ("start", start, null) };
            foreach (var f in moves.Where(m => m.Add && m.Cls == c).Select(m => m.Filter).Distinct())
            {
                var ws = start.Where(w => !moves.Any(m => m.Filter == f && !m.Add && m.Weapon == w)).ToList();
                ws.AddRange(moves.Where(m => m.Filter == f && m.Add && m.Cls == c).Select(m => m.Weapon).Where(w => !ws.Contains(w)));
                variants.Add((f.Split('/').Last(), ws, f));
            }
            classes++;
            foreach (var (vn, ws, f) in variants)
            {
                // the parts this variant shows: "always" ones, and those of its upgrade (activation / special / invent:
                // not here - a build-up that comes with an invention is no weapon change)
                var act = Compose.ActiveParts(specs, s => s.Cond == "always" || s.Cond == "ready" || (s.Cond == "upgrade" && f != null && f.TrimStart('/').EndsWith(s.ArgStr ?? "\0", StringComparison.Ordinal)));
                var gun = Compose.GunSpec(act);
                var rider = act.FirstOrDefault(s => s.Kind == "rider" && s.Pi < 0) ?? act.FirstOrDefault(s => s.Kind == "rider");
                foreach (var wid in ws)
                {
                    var w = R.WeaponAt(R.WeaponPaths(c, null).FirstOrDefault(p => p.EndsWith("/" + wid, StringComparison.Ordinal)) ?? FindWeapon(R, tt, wid), null);
                    if (w == null) continue;
                    bool ranged = w.Projectile != null;
                    var clips = w.Anims.Count > 0 ? w.Anims.Select(a => a.Name ?? "").ToList() : new List<string> { "attack_front" };
                    bool fires = ranged && gun != null;
                    var riderClip = rider == null ? null : rider.AttackAnim ?? (rider.Anim == "ride_idle_0" ? "ride_attack_front" : null);
                    // the clips the carrier plays (each of the weapon's: Combat picks one at random)
                    var played = clips.Where(x => Compose.CarrierStrikes(ranged, animal, fires, riderClip != null, R.IsStrikeOf(c, x))).ToList();
                    string gunClip = fires ? Compose.GunClips(gun!).First(x => x != null)! : "-";
                    t.Log($"{c} [{vn}] {wid}: {(ranged ? "ranged" : "melee")} -> carrier {(played.Count > 0 ? string.Join("/", played) : "stands")}, add-on {(fires ? gun!.Gfx + " " + gunClip : "-")}, rider {riderClip ?? "-"}");
                    rows++;
                    if (ranged && animal && fires && played.Count > 0) bad.Add($"{c} [{vn}] {wid}: its gun fires, the animal plays {string.Join("/", played)}");
                    if (ranged && animal && played.Any(x => R.IsStrikeOf(c, x)))
                        (riderClip == null && !fires ? known : bad).Add($"{c} [{vn}] {wid}: ranged, the animal plays its melee strike {string.Join("/", played.Where(x => R.IsStrikeOf(c, x)))}");
                    if (!ranged && played.Count != clips.Count) bad.Add($"{c} [{vn}] {wid}: a melee blow without the carrier's strike");
                    if (!ranged && fires) bad.Add($"{c} [{vn}] {wid}: the gun fires for a melee blow");
                }
            }
        }
        t.Log($"{classes} classes with add-ons, {rows} weapon rows");
        foreach (var k in known) t.Log("no one else shows the shot: " + k);
        t.True(classes > 10, "classes with add-ons found");
        t.True(bad.Count == 0, "proper attack clips:\n      " + string.Join("\n      ", bad));
    }

    static string FindWeapon(Rules R, JsonNode tt, string id)
    {
        foreach (var (tribe, tn) in tt["StartTT"]!["Objects"]!.AsObject())
            if (tn?["Weapons"]?[id] != null) return $"/Objects/{tribe}/Weapons/{id}";
        return "";
    }

    /// <summary>the ballista rhino (base) shoots a passive enemy: the rhino never strikes, its ballista plays its clip;
    /// the plain rhino (melee, the same model) strikes with "attack_front"</summary>
    public static void RhinoBallistaStands(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        if (!M.Clips("woolly_rhino").Has("attack_front")) t.Skip("no woolly_rhino model (PW_MODELS)");
        var W = Sys1.NewWorld(R, M);
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu"); var p1 = Sys1.AddPlayer(W, R, 1, "Aje");
        var bal = W.SpawnUnit("hu_rhino_ballista", p0, 0, 0)!;
        var rhi = W.SpawnUnit("hu_rhino", p0, 0, 60)!;
        var v1 = W.SpawnUnit("aje_spearman", p1, 14, 0)!; var v2 = W.SpawnUnit("aje_spearman", p1, 8, 60)!;
        v1.Hp = v2.Hp = 1e6;
        W.SetAggro(new[] { v1, v2 }, -1);
        W.Order(new[] { bal }, Order.Attack(v1));
        W.Order(new[] { rhi }, Order.Attack(v2));
        var balClips = new HashSet<string>(); var rhiClips = new HashSet<string>(); bool gunFired = false;
        for (int i = 0; i < 20 * 15; i++)
        {
            W.Step();
            if (bal.Anim?.CurName is string a) balClips.Add(a.Split('#')[0]);
            if (rhi.Anim?.CurName is string b) rhiClips.Add(b.Split('#')[0]);
            if (bal.Comp?.Gun?.Anim?.CurName == "attack_front") gunFired = true;
        }
        t.Log("ballista rhino: " + string.Join(", ", balClips) + "; rhino: " + string.Join(", ", rhiClips));
        t.True(bal.Comp?.Gun != null, "the ballista is the rhino's gun");
        t.True(v1.Hp < 1e6, "the ballista hit");
        t.True(gunFired, "the ballista played its clip");
        t.True(!balClips.Overlaps(new[] { "attack_front", "attack_left", "attack_right", "attack_01", "attack_02" }), "the ballista rhino does not strike");
        t.True(rhiClips.Contains("attack_front") || rhiClips.Contains("attack_01") || rhiClips.Contains("attack_02"), "the plain rhino strikes");
    }
}
