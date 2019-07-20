#version 300 es
precision mediump float;

in vec2 uv;
out vec4 color;

uniform sampler2D sampler;

void main()
{
  color = texture(sampler, uv).rgba;
}