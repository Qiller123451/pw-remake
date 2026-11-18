-- Containment 3: Stampede on Route 66 (east of Flagstaff, Arizona, July 2, 1964). Tranquilizers and capture trucks:
-- the horned herd into the pen, not into Flagstaff. Ported from game/sequel/missions.js mission 3.
local S = require "sequel"

return {
  setup = function(M)
    M.state.need = S.by(M, { 3, 5, 6 })
    M.state.through = 0
    M.pen("pen", -150, 70)
    M.mark(-170, 150, "pen", 0x7dff6a)
    M.objective("capture", "Capture horned dinosaurs: tranquilize them, haul them to the pen", { of = M.state.need })
    M.objective("town", "Keep the herd out of Flagstaff (no more than 6 may reach it)", {})
    M.rift("desert", 230, -60, { every = 45, first = 20, target = { -230, 10 }, cap = 14, waves = {
      { { "Triceratops", 2 } }, { { "Pentaceratops", 2 }, { "Triceratops", 1 } }, { { "Styracosaurus", 3 } }, { { "Triceratops", 2 }, { "Velociraptor", 3 } } } })
    M.say("booker", "Colonel. Don't kill the big ones - dart 'em first. Once a dart's in, shoot all you like: when it should die, it goes to sleep instead.")
    M.say("booker", "Then a truck pulls up, we winch 'em aboard and drive 'em to the pen. Gentle as you please.")
    M.say("halloran", "And if one wakes up in the truck?")
    M.say("booker", "Then you'll hear about it, ma'am.")
  end,
  asleep = function(M)
    if not M.state.tip then M.state.tip = true M.say("booker", "One's down! Get a capture truck next to it - right-click the sleeping animal with the truck.") end
  end,
  captured = function(M, a)
    M.progress("capture", a.n)
    if a.n == 1 then M.say("booker", "That's one. She's yours now, Colonel - a tame triceratops fights for whoever feeds her.") end
  end,
  tick = function(M)
    local town = M.zone("town")
    for _, id in ipairs(M.units("outbreak", { cls = "ANML" })) do
      local u = M.info(id)
      if not u.intown and S.near(u, town.x, town.z, town.r) then
        M.set(id, "intown", true)
        M.state.through = M.state.through + 1
        M.notify("A dinosaur reached Flagstaff (" .. M.state.through .. ")", "bad")
      end
    end
    if M.state.through > 6 then M.fail("town") end
    if M.is_done("capture") and not M.state.won then
      M.state.won = true
      M.complete("town")
      M.say("booker", "Pen's full. These girls will pull a howitzer by Christmas.")
      M.after(8, "end")
    end
  end,
  ["end"] = function(M) M.win("The herd is in SEAS hands.") end,
}
