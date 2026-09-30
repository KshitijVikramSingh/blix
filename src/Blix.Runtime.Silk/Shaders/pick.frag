#version 450

// Writes the draw's ID, 24 bits across R, G and B of an RGBA8 target, so the host can read back which
// selectable is nearest at that pixel. 0 is the clear colour and means nothing was there; IDs start at 1.
// k/255 is exact in UNORM8, so the bytes survive the round trip.
layout(push_constant) uniform Push {
    layout(offset = 64) uint uId;
};

layout(location = 0) out vec4 outId;

void main() {
    outId = vec4(float(uId & 0xFFu), float((uId >> 8) & 0xFFu), float((uId >> 16) & 0xFFu), 255.0) / 255.0;
}
