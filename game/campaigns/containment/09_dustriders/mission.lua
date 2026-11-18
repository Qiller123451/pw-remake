-- Containment 9: The Dustriders (Monument Valley, Arizona, February 11, 1965). Tala's camp, Crane's hunters - who is
-- the enemy? Ported from game/sequel/missions.js mission 9.
local S = require "sequel"
local T = S.T

return {
  setup = function(M)
    local a = "aje"
    M.state.ajeHq = S.first(M.spawn("aje_resource_collector", a, 170, -170, 1))
    M.building("aje_rodeo", a, 130, -170, 0) M.building("aje_tent", a, 200, -130, 0) M.building("aje_tent", a, 140, -130, 0)
    M.spawn("sq_tala", a, 160, -140, 1)
    M.spawn("aje_warrior", a, 150, -120, 6, { level = 2 }) M.spawn("aje_archer", a, 170, -120, 4, { level = 2 })
    M.state.craneHq = M.building("seas_headquarters", "crane", 180, 190, math.pi)
    M.building("seas_barracks", "crane", 140, 200, 0) M.building("seas_turret_tower", "crane", 150, 160, 0)
    M.spawn("seas_gunner", "crane", 160, 160, 5, { level = 2 })
    M.objective("meet", "Find the Dustriders' camp (north-east)", {})
    M.say("taslov", "They came through a rift, the same as the animals. They are as lost as we would be over there.")
    M.say("booker", "Lost people with spears pointed at us, Doc.")
  end,
  tick = function(M)
    if not M.state.met and #M.near(170, -160, 90, "me") > 0 then
      M.state.met = true
      M.complete("meet")
      M.say("tala", "Sky-riders. You smell like the men who chained my hunters.")
      M.say("halloran", "Those men are no friends of ours. They are coming for you. We can stand with you, or watch.")
      M.say("tala", "Then stand. If you lie, my raptors will know.")
      M.relation("aje", "me", 2)
      M.objective("defend", "Keep Tala's camp standing", {})
      M.objective("hunters", "Destroy the hunters' base in the south-east", {})
      M.after(40, "raid")
    end
    if M.state.met and not S.alive(M, M.state.ajeHq) then M.fail("defend") end
    if M.state.met and not S.alive(M, M.state.craneHq) and not M.state.won then
      M.state.won = true M.complete("hunters") M.complete("defend")
      M.say("tala", "You did not lie. My riders will go where you go, sky-woman.")
      M.after(10, "end")
    end
  end,
  raid = function(M)
    M.say("kiko", "Trucks on the south road. Crane's hunters, with cages!")
    M.spawn("seas_gunner", "crane", 200, 220, 6, { level = 2, target = { 170, -150 } })
    M.spawn("seas_mobile_suit", "crane", 210, 230, 2, { target = { 170, -150 } })
    M.after(3 * T, "raid")
  end,
  ["end"] = function(M) M.win("SEAS has new allies.") end,
}
