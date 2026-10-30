// Pixel shading shared by sprite and ribbon effects. Unlit: the colour is what the artist authored.
//
// The scene depth is read as a texture rather than bound as the depth attachment, which lets the same read both
// depth-test the effect and soften where it meets geometry.

import "vfx_common.slang";
import "../Common/common.slang";
import "../Common/common_bindless.slang";

struct VfxVertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float4 color : TEXCOORD1;
    nointerpolation uint2 textureAndSampler : TEXCOORD2;
    // View depth of this vertex, then the emitter's soft particle distance.
    float2 depthAndSoftness : TEXCOORD3;
};

VfxVertexOutput VfxCulledVertex()
{
    VfxVertexOutput output;
    // Behind the near plane on both backends, so every vertex of a dead particle is clipped.
    output.position = float4(0.0f, 0.0f, -1.0f, 1.0f);
    output.uv = float2(0.0f, 0.0f);
    output.color = float4(0.0f, 0.0f, 0.0f, 0.0f);
    output.textureAndSampler = uint2(VFX_NO_TEXTURE, 0u);
    output.depthAndSoftness = float2(0.0f, 0.0f);
    return output;
}

float VfxLinearViewDepth(float depth, float4x4 invProjection)
{
    float4 view = mul(float4(0.0f, 0.0f, depth, 1.0f), invProjection);
    return view.z / view.w;
}

// Coverage for an effect with no texture: a soft disc for sprites, soft edges across a ribbon.
float VfxUntexturedCoverage(float2 uv, bool ribbon)
{
    if (ribbon)
    {
        float edge = min(uv.y, 1.0f - uv.y);
        return saturate(edge * 6.0f);
    }

    float2 centred = uv * 2.0f - 1.0f;
    float radiusSquared = dot(centred, centred);
    float falloff = saturate(1.0f - radiusSquared);
    return falloff * falloff;
}

float4 VfxShade(
    VfxVertexOutput input,
    uint depthHandle,
    float4x4 invProjection,
    bool additive,
    bool ribbon)
{
    uint2 pixel = uint2(input.position.xy);
    float sceneDepth = GetTexture(depthHandle).Load(int3(pixel, 0)).r;
    if (input.position.z > sceneDepth)
    {
        discard;
    }

    float fade = 1.0f;
    float softness = input.depthAndSoftness.y;
    if (softness > 0.0f && IsBackgroundDepth(sceneDepth) == false)
    {
        float sceneViewDepth = VfxLinearViewDepth(sceneDepth, invProjection);
        fade = saturate((sceneViewDepth - input.depthAndSoftness.x) / softness);
    }

    float4 texel;
    if (input.textureAndSampler.x != VFX_NO_TEXTURE)
    {
        texel = GetTexture(input.textureAndSampler.x).Sample(GetSampler(input.textureAndSampler.y), input.uv);
    }
    else
    {
        texel = float4(1.0f, 1.0f, 1.0f, VfxUntexturedCoverage(input.uv, ribbon));
    }

    float4 color = input.color * texel;
    color.a *= fade;
    if (color.a <= 0.0f)
    {
        discard;
    }

    if (additive)
    {
        // Additive pipelines blend One, One. Writing zero alpha leaves the target's alpha untouched.
        return float4(color.rgb * color.a, 0.0f);
    }

    return color;
}
