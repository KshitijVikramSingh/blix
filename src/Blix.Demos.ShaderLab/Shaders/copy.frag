#version 410 core

in vec2 textureCoordinate;
out vec4 fragmentColor;
uniform sampler2D uSceneTexture;

// Trivial HDR copy. Sits between the opaque scene pass and the glass pass: the glass
// shader needs to sample "what was behind me", but you can't read from the same texture
// you're writing to. This snapshots the opaque scene into sceneCopySurface, leaving
// the live scene buffer free for the glass pass to write into.
void main()
{
    fragmentColor = texture(uSceneTexture, textureCoordinate);
}
