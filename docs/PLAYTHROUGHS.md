# Campaign playthroughs

The scripted playthroughs (`tests/PwRemake.Tests/Playthrough`, ports of the browser remake's
`tests/campaign_play_NN.js`; BoosterPack 1's 41-43 and MIRAGE's SEAS missions 20-23 written from their triggers) play
every mission to its end with each tribe in both configurations (docs/TESTING.md "Playthroughs"). Built from
`tests/PwRemake.Tests/Playthrough/results/*.json` by the runner; do not edit by hand. Rows in the menu's order (index.json ids).

A cell: **won** = the mission was played to its end and won (game minutes; cheats: teleports / cheat kills / top-ups;
a step whose own check failed on the way is named), else the step it stopped at (steps passed / all).

| mission | base Hu | base Aje | base Ninigi | mirage Hu | mirage Aje | mirage Ninigi | mirage SEAS |
|---|---|---|---|---|---|---|---|
| 0 Mission 0: Tutorial | **won** 21.9 min (3/7/0) | **won** 19.4 min (6/6/2) | **won** 20.3 min (6/6/2) | **won** 20 min (5/7/0) | **won** 18.9 min (5/6/2) | **won** 20.1 min (6/5/0) | **won** 21.9 min (4/8/0) |
| 1 Mission 1: Stranded | **won** 5.6 min (0/62/0) | **won** 5.8 min (0/57/0) | **won** 6 min (0/63/0) | **won** 6 min (0/69/0) | **won** 6 min (0/68/0) | **won** 6.4 min (0/74/0) | **won** 6.1 min (0/70/0) |
| 2 Mission 2: Druid Island | **won** 6 min (0/88/0) | **won** 6.1 min (0/104/0) | **won** 7.4 min (0/100/0) | **won** 6.1 min (0/91/0) | **won** 6.6 min (0/99/0) | **won** 6 min (0/80/0) | **won** 6.5 min (0/87/0) |
| 3 Mission 3: Amazon Island | **won** 1.8 min (0/34/0) | **won** 1.8 min (0/36/0) | **won** 1.9 min (0/37/0) | **won** 3 min (0/47/0) | **won** 2.2 min (0/49/0) | **won** 2.7 min (1/42/0) | **won** 2.3 min (0/42/0) |
| 4 Mission 4: The Dustriders | **won** 1.4 min (7/11/0) | **won** 1.4 min (5/1/0) | **won** 1.4 min (5/10/0) | **won** 1.4 min (6/21/0) | **won** 1.4 min (7/21/0) | **won** 1.5 min (8/20/0) | **won** 1.4 min (7/21/0) |
| 41 Mission 4-1: The Devil's Advocate | **won** 29.3 min (0/322/41) | **won** 62.9 min (67/773/40) | **won** 37.5 min (18/271/28) | **won** 21.9 min (27/174/35) | **won** 45.9 min (5/520/33) | **won** 31.7 min (29/428/25) | **won** 20.1 min (22/181/35) |
| 42 Mission 4-2: A Cold Day In Hell | **won** 9.4 min (0/47/0) | **won** 9.5 min (0/45/0) | **won** 9.4 min (0/50/0) | **won** 9 min (0/54/0) | **won** 9.1 min (0/54/0) | **won** 9.1 min (0/54/0) | **won** 9.2 min (0/58/0) |
| 43 Mission 4-3: Operation Neptune | **won** 3.7 min (2/34/0) | **won** 4.4 min (13/27/0) | **won** 4.4 min (19/34/0) | **won** 3.5 min (1/31/0) | **won** 4.2 min (1/29/0) | **won** 4 min (4/29/0) | **won** 4 min (1/30/0) |
| 5 Mission 5: The Holy City | **won** 4.6 min (0/102/0) | **won** 4.4 min (0/111/0) | **won** 4.6 min (0/102/0) | **won** 5.2 min (0/114/0) | **won** 5.3 min (0/103/0) | **won** 5.2 min (0/110/0) | **won** 5.3 min (0/107/0) |
| 6 Mission 6: Pirates and Hostages | **won** 17.6 min (6/147/22) | **won** 16.1 min (6/174/20) | **won** 25.5 min (7/153/12) | **won** 18.4 min (12/156/23) | **won** 9.3 min (3/165/14) | **won** 4.1 min (3/66/7) | **won** 18.3 min (13/186/23) |
| 7 Mission 7: The Water Temple | **won** 8.9 min (4/30/0) | **won** 8.9 min (6/40/0) | **won** 9 min (6/24/0) | **won** 8.9 min (5/29/0) | **won** 8.9 min (7/33/0) | **won** 8.9 min (7/28/0) | **won** 8.8 min (4/27/0) |
| 8 Mission 8: Valley of the Gods | **won** 6.3 min (0/125/0) | **won** 6.5 min (0/130/0) | **won** 6.3 min (0/126/0) | **won** 6.5 min (0/150/0) | **won** 6.5 min (0/158/0) | **won** 6.5 min (0/145/0) | **won** 6.5 min (0/149/0) |
| 9 Mission 9: Perilous Path | **won** 26.2 min (45/167/0) | **won** 26.8 min (43/200/0) | **won** 74.7 min (39/540/11) | **won** 71.2 min (20/470/17) | **won** 36.4 min (17/453/21) | **won** 54.1 min (13/579/13) | **won** 71.4 min (16/620/16) |
| 10 Mission 10: The Prophet | **won** 3.3 min (0/51/0) | **won** 3 min (0/36/0) | **won** 3.1 min (0/33/0) | **won** 3.4 min (0/58/0) | **won** 3.4 min (0/58/0) | **won** 3.4 min (0/58/0) | **won** 3.4 min (0/58/0) |
| 11 Mission 11: Arena | **won** 17.7 min (0/0/0) | **won** 17.7 min (0/0/0) | **won** 17.7 min (0/0/0) | **won** 18.4 min (0/0/0); 1 check(s) failed: the level-4 Atroxosaurus enters (1092 s, level 5) | **won** 18.4 min (0/0/0); 1 check(s) failed: the level-4 Atroxosaurus enters (1092 s, level 5) | **won** 18.4 min (0/0/0); 1 check(s) failed: the level-4 Atroxosaurus enters (1092 s, level 5) | **won** 18.4 min (0/0/0); 1 check(s) failed: the level-4 Atroxosaurus enters (1092 s, level 5) |
| 12 Mission 12: Sea Battles | **won** 42.4 min (0/43/0) | **won** 42.4 min (0/90/2) | **won** 42.4 min (0/40/0) | **won** 42.4 min (0/40/0) | **won** 42.4 min (0/89/0) | **won** 42.4 min (0/31/0) | **won** 42.4 min (0/62/0) |
| 13 Mission 13: The Rush | **won** 49.4 min (35/188/0) | **won** 50.2 min (26/185/0) | **won** 50.4 min (36/189/0) | **won** 50 min (6/250/0) | **won** 47.8 min (84/244/0) | **won** 48.5 min (114/233/0) | **won** 49.6 min (7/233/0) |
| 14 Mission 14: Prisoners | **won** 4.8 min (0/65/0) | **won** 4.6 min (0/70/0) | **won** 4 min (0/61/0) | **won** 3.8 min (3/35/0) | **won** 4.1 min (3/40/0) | **won** 4.2 min (0/52/0) | **won** 3.9 min (2/36/0) |
| 15 Mission 15: New World Order  | **won** 9.6 min (46/213/1) | **won** 13.2 min (92/263/8) | **won** 10 min (21/214/3) | **won** 16.9 min (70/433/2) | **won** 16.1 min (101/329/73) | **won** 20.8 min (37/417/8) | **won** 13.7 min (57/348/2) |
| 16 Mission 16: Showdown! | **won** 5.9 min (0/46/0) | **won** 6.9 min (0/49/0) | **won** 4.1 min (0/37/0) | **won** 5.1 min (0/48/0) | **won** 5.1 min (0/48/0) | **won** 5.1 min (0/48/0) | **won** 5.1 min (0/48/0) |
| 20 Mission 4-1: The Devil's Advocate | - | - | - | **won** 27.8 min (26/380/42) | **won** 39.5 min (5/446/37) | **won** 38.5 min (18/352/22) | **won** 25.9 min (36/347/36) |
| 21 Mission 4-2: A Cold Day In Hell | - | - | - | **won** 8.7 min (0/51/0) | **won** 7.5 min (0/68/0) | **won** 8.8 min (0/55/0) | **won** 8.6 min (0/56/0) |
| 22 Mission 4-3: Operation Neptune | - | - | - | **won** 3.4 min (1/25/0) | **won** 3.6 min (15/26/0) | **won** 3.5 min (15/28/0) | **won** 3.5 min (7/26/0) |
| 23 Mission 1: Seeking Ada | - | - | - | **won** 6.6 min (2/89/0) | **won** 7.2 min (3/109/0) | **won** 8.2 min (1/128/0) | **won** 7.6 min (0/106/0) |

Runs: 156, finished 156; dates 2026-11-03, 2026-11-05, 2026-11-12, 2026-11-20. Difficulty 1 (normal), seed 1.
