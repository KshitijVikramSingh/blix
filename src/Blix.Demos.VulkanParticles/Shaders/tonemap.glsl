// Filmic tonemap operators. Currently just ACES (the Knarkowicz fit);
// AgX / Reinhard / Neutral can land alongside as additional functions
// when present.frag wants to expose a tonemap selector like SponzaModern.

vec3 acesTonemap(vec3 x) {
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}
