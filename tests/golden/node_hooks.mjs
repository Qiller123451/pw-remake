// node --import ./node_hooks.mjs ...: lets the remake's game modules load in node without three.js (the map code
// imports engine/terrain.js and game/prop.js, which import three for drawing only).
import { register } from 'node:module';
register(new URL('./three_resolve.mjs', import.meta.url));
