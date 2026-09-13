#version 450

// <b>The darkening that makes a thing stand on the ground rather than lie on top of it.</b>
//
// Reported as trees and buildings looking "placed atop the ground instead of built on it", which is exactly
// what an object with no contact shading looks like: a sun shadow tells you where the light is not, and
// says nothing about the ground being closed off from the sky in the crack where two surfaces meet. That
// crack is the darkest part of any real scene and its absence is the single loudest tell of a composited
// image.
//
// A disc rather than anything cleverer, because the shape of the occlusion under a tree or a wall is a disc
// to within far more than this is worth. Squared falloff so it is tight at the object and gone within a
// radius or so — a wide soft blob reads as a stain, and the thing that sells contact is that it is *tight*.

layout(location = 0) in float vRadius;
layout(location = 1) in float vStrength;

layout(location = 0) out vec4 outColor;

void main() {
    float fade = 1.0 - clamp(vRadius, 0.0, 1.0);
    // Squared, then squared again at the centre, so the darkening is concentrated where the object actually
    // touches and the rim disappears into nothing rather than ending.
    float weight = fade * fade;
    outColor = vec4(0.0, 0.0, 0.0, weight * weight * vStrength);
}
