namespace Blix.Graphics;

// Index element width. UInt16 is the default and covers ~99% of authored
// meshes; assets with >65535 vertices in a single primitive (the curtains
// pack from Khronos Sponza Modern is the canonical case) need UInt32.
// The backend tracks the format per IndexBufferHandle so DrawElements
// picks the right GL element type without the caller telling it again.
public enum IndexFormat
{
    UInt16 = 0,
    UInt32,
}
