#version 410 core

// Depth-only output. The bound framebuffer has no color attachments, so the
// rasterizer just writes gl_FragDepth implicitly.
void main()
{
}
