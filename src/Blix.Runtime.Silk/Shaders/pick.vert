#version 450

// The pick pass (PickRenderer): every selectable's geometry, drawn into the one pixel under the cursor.
// Positions only. uMvp is model * view * the pixel crop (ViewPicking.PixelCrop), which makes that pixel
// fill the 1x1 target, so the rasteriser samples exactly its centre and clips everything else.
layout(push_constant) uniform Push {
    mat4 uMvp;
};

layout(location = 0) in vec3 inPosition;

void main() {
    gl_Position = uMvp * vec4(inPosition, 1.0);
}
