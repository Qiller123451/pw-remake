-- The triggers of "Outbreak" (written by tools/outbreak - the generator; the mission's logic is custom.lua's).
-- docs/OUTBREAK.md, docs/LUA_MAPS.md

-- the night falls: the dialogue the first time (custom.lua sends the waves at each dusk)
trigger "Night falls (first)" {
  enabled = true, once = true,
  conditions = { DayPhase{ phase = "dusk", night = "==1" } },
  actions = { Dialog{ scene = "outbreak/ob_dusk.dlg" }, Sound{ soundname = "ui_quest_new", player = -1 } },
}

-- the first dawn: they withdraw
trigger "First dawn" {
  enabled = true, once = true,
  conditions = { DayPhase{ phase = "dawn", night = "==1" } },
  actions = { Dialog{ scene = "outbreak/ob_dawn.dlg" } },
}