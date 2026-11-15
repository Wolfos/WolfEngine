// The shader interface WolfEngine offers game compute shaders: `import wolf;`.
//
// This module is a contract with game code. Engine shaders under ../ change freely; this file changes only
// deliberately, and a change here is a breaking change for every game.
//
// Constant buffers: the engine fills WolfView at b12. Declare your own parameters in cbuffers at b0, b1, b2 or
// b11; the importer rejects any other register.

__exported import "../Common/common_bindless.slang";

struct WolfViewData
{
    // Camera-relative: it maps clip space to a position relative to cameraOrigin, not to the world origin.
    row_major float4x4 invViewProjection;
    float3 cameraOrigin;
    float padding0;
    // Size of the scene colour and depth textures, in pixels. See WolfRenderSize.
    uint renderSizeX;
    uint renderSizeY;
    uint2 padding1;
};

cbuffer WolfView : register(b12)
{
    WolfViewData wolfView;
};

uint2 WolfRenderSize()
{
    return uint2(wolfView.renderSizeX, wolfView.renderSizeY);
}

// True for pixels no geometry was drawn into.
bool WolfIsBackgroundDepth(float depth)
{
    return depth >= 0.999999f;
}

// World-space position of the surface drawn at a pixel, from its depth.
float3 WolfReconstructWorldPosition(uint2 pixel, float depth)
{
    float2 ndc;
    ndc.x = (float(pixel.x) + 0.5f) / float(wolfView.renderSizeX) * 2.0f - 1.0f;
    ndc.y = -((float(pixel.y) + 0.5f) / float(wolfView.renderSizeY) * 2.0f - 1.0f);
    float4 positionH = mul(float4(ndc, depth, 1.0f), wolfView.invViewProjection);
    return positionH.xyz / positionH.w + wolfView.cameraOrigin;
}
