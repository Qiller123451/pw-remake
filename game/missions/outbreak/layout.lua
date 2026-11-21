-- The layout of "Outbreak" (written by tools/outbreak, the generator - do not edit: run it again). Data only.
-- Map coordinates (metres from the north-west corner, x east, y south). docs/OUTBREAK.md
return {
  centre = { 384, 384 },
  base_r = 120.65,
  infected = 1,
  camps = {
    { name = "A", x = 384, y = 324, entrance_out = { 408.22, 208.59 }, entrance_in = { 384, 270.89 }, tree = { 414, 300 }, stone = { 354, 302 } },
    { name = "B", x = 444, y = 384, entrance_out = { 552.32, 358.19 }, entrance_in = { 493.3, 384 }, tree = { 468, 414 }, stone = { 466, 354 } },
    { name = "C", x = 341.57, y = 426.43, entrance_out = { 240.49, 503.99 }, entrance_in = { 296.49, 471.51 }, tree = { 303.39, 422.18 }, stone = { 347.23, 463.2 } },
  },
  barrier = { entrance = 3, x = 277.02, y = 484.08, rocks = { "OB_Rockfall_01", "OB_Rockfall_02", "OB_Rockfall_03", "OB_Rockfall_04", "OB_Rockfall_05", "OB_Rockfall_06", "OB_Rockfall_07", "OB_Rockfall_08", "OB_Rockfall_09", "OB_Rockfall_10", "OB_Rockfall_11", "OB_Rockfall_12", "OB_Rockfall_13", "OB_Rockfall_14", "OB_Rockfall_15", "OB_Rockfall_16", "OB_Rockfall_17", "OB_Rockfall_18", "OB_Rockfall_19", "OB_Rockfall_20", "OB_Rockfall_21", "OB_Rockfall_22", "OB_Rockfall_23", "OB_Rockfall_24", "OB_Rockfall_25", "OB_Rockfall_26", "OB_Rockfall_27", "OB_Rockfall_28", "OB_Rockfall_29", "OB_Rockfall_30", "OB_Rockfall_31", "OB_Rockfall_32", "OB_Rockfall_33", "OB_Rockfall_34", "OB_Rockfall_35", "OB_Rockfall_36", "OB_Rockfall_37", "OB_Rockfall_38" } },
  lords = {
    { name = "OB_Boss_Lord", kind = "lord", village = 11, x = 179.67, y = 179.03 },
    { name = "OB_Boss_Beast", kind = "beast", village = 9, x = 623.92, y = 609.28 },
  },
  villages = {
    { name = "Village_1", size = "outpost", x = 534.23, y = 237.27, r = 24, entrance = 2, alt = 2, stage = 2, buildings = { "V1_hu_small_tower_1", "V1_hu_stone_cottage_2", "V1_hu_stone_cottage_3", "V1_hu_kennel_4" } },
    { name = "Village_2", size = "outpost", x = 490.67, y = 567.21, r = 36, entrance = 2, alt = 2, stage = 3, buildings = { "V2_hu_bunker_1", "V2_hu_stone_cottage_2", "V2_hu_lumberjack_cottage_3", "V2_hu_corn_field_4", "V2_hu_stone_cottage_5" } },
    { name = "Village_3", size = "outpost", x = 190.55, y = 302.28, r = 24, entrance = 3, alt = 1, stage = 2, buildings = { "V3_hu_small_tower_1", "V3_hu_stone_cottage_2", "V3_hu_stone_quarry_3", "V3_hu_magic_cauldron_4" } },
    { name = "Village_4", size = "outpost", x = 346.96, y = 594.77, r = 27, entrance = 3, alt = 2, stage = 3, buildings = { "V4_hu_bunker_1", "V4_hu_stone_cottage_2", "V4_hu_kennel_3", "V4_hu_stone_cottage_4", "V4_hu_small_tower_5" } },
    { name = "Village_5", size = "village", x = 283.19, y = 122.78, r = 49, entrance = 1, alt = 1, stage = 3, buildings = { "V5_hu_tavern_1", "V5_hu_stone_cottage_2", "V5_hu_stone_cottage_3", "V5_hu_stone_cottage_4", "V5_hu_small_animal_farm_5", "V5_hu_lumberjack_cottage_6", "V5_hu_small_tower_7", "V5_hu_kennel_8" } },
    { name = "Village_6", size = "village", x = 645.75, y = 483.44, r = 37, entrance = 2, alt = 2, stage = 2, buildings = { "V6_hu_weapons_smith_1", "V6_hu_stone_cottage_2", "V6_hu_stone_cottage_3", "V6_hu_stone_cottage_4", "V6_hu_corn_field_5", "V6_hu_corn_field_6", "V6_hu_large_tower_7" } },
    { name = "Village_7", size = "village", x = 109.35, y = 427.01, r = 43, entrance = 3, alt = 1, stage = 3, buildings = { "V7_hu_warehouse_1", "V7_hu_stone_cottage_2", "V7_hu_stone_cottage_3", "V7_hu_stone_quarry_4", "V7_hu_machine_maker_5", "V7_hu_bunker_6", "V7_hu_magic_cauldron_7" } },
    { name = "Village_8", size = "village", x = 424.4, y = 659.05, r = 49, entrance = 3, alt = 2, stage = 3, buildings = { "V8_hu_tavern_1", "V8_hu_stone_cottage_2", "V8_hu_stone_cottage_3", "V8_hu_stone_cottage_4", "V8_hu_lumberjack_cottage_5", "V8_hu_small_animal_farm_6", "V8_hu_small_tower_7", "V8_hu_bunker_8" } },
    { name = "Village_9", size = "town", x = 610.92, y = 631.8, r = 52, entrance = 2, alt = 2, stage = 3, buildings = { "V9_hu_temple_1", "V9_hu_arena_2", "V9_hu_stone_cottage_3", "V9_hu_stone_cottage_4", "V9_hu_stone_cottage_5", "V9_hu_stone_cottage_6", "V9_hu_warehouse_7", "V9_hu_weapons_smith_8", "V9_hu_large_tower_9", "V9_hu_large_tower_10", "V9_hu_kennel_11", "V9_hu_corn_field_12" } },
    { name = "Village_10", size = "town", x = 151.61, y = 621.1, r = 52, entrance = 3, alt = 2, stage = 3, buildings = { "V10_hu_fireplace_1", "V10_hu_tavern_2", "V10_hu_stone_cottage_3", "V10_hu_stone_cottage_4", "V10_hu_stone_cottage_5", "V10_hu_machine_maker_6", "V10_hu_small_animal_farm_7", "V10_hu_corn_field_8", "V10_hu_large_tower_9", "V10_hu_bunker_10", "V10_hu_magic_cauldron_11" } },
    { name = "Village_11", size = "town", x = 138.1, y = 155.03, r = 55, entrance = 1, alt = 1, stage = 3, buildings = { "V11_hu_arena_1", "V11_hu_warpgate_2", "V11_hu_stone_cottage_3", "V11_hu_stone_cottage_4", "V11_hu_stone_cottage_5", "V11_hu_temple_6", "V11_hu_lumberjack_cottage_7", "V11_hu_stone_quarry_8", "V11_hu_large_tower_9", "V11_hu_small_tower_10" } },
    { name = "Village_12", size = "town", x = 614.95, y = 145.49, r = 52, entrance = 1, alt = 1, stage = 3, buildings = { "V12_hu_temple_1", "V12_hu_warehouse_2", "V12_hu_stone_cottage_3", "V12_hu_stone_cottage_4", "V12_hu_stone_cottage_5", "V12_hu_stone_cottage_6", "V12_hu_tavern_7", "V12_hu_weapons_smith_8", "V12_hu_small_animal_farm_9", "V12_hu_large_tower_10", "V12_hu_bunker_11" } },
  },
}
