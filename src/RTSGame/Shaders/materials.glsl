// The material classes, matching SettlementArt.MaterialClass.
//
// Carried in the instance colour's fourth channel because an opaque pass has no use for alpha, which keeps
// a material system out of the shared InstanceData struct — see the remarks there.
//
// Shared between the vertex and fragment stages rather than declared in each: the vertex stage needs to
// know what a surface is too, now that foliage bends in the wind, and a second copy of a table of magic
// constants is a table that will drift. Game-specific on purpose, so it lives here and not in
// src/Blix.Shaders, which is the engine's library and has no opinion about wheat.

#ifndef RTS_MATERIALS_GLSL
#define RTS_MATERIALS_GLSL

const float kTerrain = 0.05;
const float kCrafted = 0.15;
const float kPlaster = 0.25;
const float kRoof    = 0.35;
const float kTimber  = 0.45;
const float kStone   = 0.55;
const float kFoliage = 0.65;
const float kCrop    = 0.75;
const float kBody    = 0.85;
// Anything that makes its own light: a lit window, a lantern at a doorway, embers at a work site. The
// only class the sun is not allowed to have an opinion about.
const float kEmber   = 0.95;

bool isClass(float carried, float which) { return abs(carried - which) < 0.05; }

// Whether a surface is a growing thing, which is the one question both stages ask: the vertex stage to
// bend it and the fragment stage to shade it as a leaf rather than as a tile.
bool isPlant(float carried) { return isClass(carried, kFoliage) || isClass(carried, kCrop); }

#endif
