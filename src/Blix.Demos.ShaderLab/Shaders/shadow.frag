#version 410 core

// Depth-only output. The bound framebuffer has no color attachments, so this
// shader only needs to let the rasterizer write gl_FragDepth implicitly.
void main()
{
}
