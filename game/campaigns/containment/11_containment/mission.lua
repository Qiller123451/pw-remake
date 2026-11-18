-- Containment 11: Containment (the last mission of 1964-65). Protect the Gate machine for twenty minutes while every
-- rift on Earth sends what it has - and Crane comes for the machine. Ported from game/sequel/missions.js mission 11.
local S = require "sequel"
local T = S.T

return {
  setup = function(M)
    M.state.machine = M.building("sq_gate_machine", "me", 0, 0, 0)
    M.state.full = 20 * T
    M.set_clock(M.state.full, "Full charge in")
    M.spawn("sq_tala", "aje", 40, 60, 1) M.spawn("aje_warrior", "aje", 50, 60, 6, { level = 3 }) M.spawn("aje_archer", "aje", 60, 70, 4, { level = 3 })
    local t = M.state.machine
    local waves = { { { "Velociraptor", 5 } }, { { "Deinonychus", 4 }, { "Allosaurus", 1 } }, { { "Carnotaurus", 2 }, { "Velociraptor", 4 } },
      { { "Atroxosaurus", 1 }, { "Deinonychus", 4 } }, { { "Spinosaurus", 1 }, { "Allosaurus", 2 } }, { { "Triceratops", 3 }, { "Smilodon", 3 } } }
    M.rift("north", 0, -210, { every = 120, first = 240, target = t, waves = waves })
    M.rift("east", 210, -20, { every = 130, first = 300, target = t, waves = waves })
    M.rift("west", -210, -40, { every = 140, first = 360, target = t, waves = waves })
    M.objective("machine", "Protect the Gate machine until it is fully charged (20:00)", {})
    M.say("taslov", "It is charging. Every rift on Earth can feel it. They will all come here.")
    M.say("tala", "Let them come. My riders have not had a good fight since the jungle.")
    M.after(9 * T, "crane") M.after(15 * T, "crane2") M.after(12 * T, "faster")
  end,
  faster = function(M)
    for _, r in ipairs(M.rifts()) do M.rift_set(r.id, "every", r.every * 0.75) end
    M.rift("south", 120, 210, { every = 100, first = 30, target = M.state.machine, waves = { { { "Allosaurus", 2 }, { "Velociraptor", 4 } }, { { "Atroxosaurus", 1 }, { "Carnotaurus", 2 } } } })
    M.say("kiko", "A fourth one just opened - south, by the farms!")
  end,
  crane = function(M)
    M.say("crane", "Margaret! Hand me the machine and I will make you rich. Refuse and I will take it anyway.")
    M.say("halloran", "Come and take it.")
    M.spawn("sq_crane", "crane", 220, 200, 1, { target = { 0, 0 } })
    M.spawn("seas_gunner", "crane", 230, 210, 8, { level = 3, target = { 0, 0 } }) M.spawn("seas_mobile_suit", "crane", 240, 190, 3, { target = { 0, 0 } })
    M.spawn("seas_wehrspinne", "crane", 220, 230, 1, { target = { 0, 0 } })
  end,
  crane2 = function(M)
    M.spawn("seas_rocketman", "crane", -230, 210, 8, { level = 3, target = { 0, 0 } }) M.spawn("seas_wehrspinne", "crane", -220, 220, 2, { target = { 0, 0 } })
    M.say("kiko", "Crane's second column from the south-west!")
  end,
  tick = function(M)
    if not S.alive(M, M.state.machine) then M.fail("machine") end
    local left = M.state.full - M.time
    if left <= 0 and not M.state.won then
      M.state.won = true
      M.complete("machine")
      for _, r in ipairs(M.rifts()) do M.close_rift(r.id) end
      M.kill(M.units("outbreak"))
      M.say("taslov", "Full charge... and - closed. All of them. Every single one.")
      M.say("halloran", "Containment achieved. Somebody tell Washington the dinosaurs are going home.")
      M.say("tala", "And my people? Our door is closed too.")
      M.say("taslov", "Doors can be opened carefully, my friend. That is a different machine. Give me a year.")
      M.after(14, "end")
    end
  end,
  ["end"] = function(M) M.win("The rifts are closed. The world has a new neighbour - and a door it can knock on.") end,
}
