// Texel provenance (SponzaLoop.TexelPicker.cs): a byte a texel id -- the low nibble the stage that last wrote its sun
// part, the high its sky part (the codes: SponzaLoop.TexelPicker.cs, ProvenanceNames). The includer declares
//   layout(std430, ...) buffer TexelProvenance { uint provenance[]; };
// first. Written by every pass that writes the parts, read by the picker and the provenance view (incident_clipmap.frag).
#ifndef PROVENANCE_GLSL
#define PROVENANCE_GLSL

void provenanceMark(uint id, uint code, bool sun, bool sky) {
    if (!sun && !sky) return;
    uint shift = 8u * (id % 4u);
    uint mask = (sun ? 0xFu : 0u) | (sky ? 0xF0u : 0u);
    uint value = ((sun ? code : 0u) | (sky ? code << 4u : 0u)) & mask;
    atomicAnd(provenance[id / 4u], ~(mask << shift));
    atomicOr(provenance[id / 4u], value << shift);
}

#endif
