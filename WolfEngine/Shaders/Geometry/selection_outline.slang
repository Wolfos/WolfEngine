// Editor selection outline, drawn as an inverted hull.
//
// The mesh is drawn front-face-culled with each vertex pushed outward in clip
// space, so the hull is a few pixels larger on screen than the real object.
// The interior is then rejected by comparing against the scene depth the
// GBuffer already produced: inside the silhouette the hull's back faces sit
// behind the object's front faces and are discarded, leaving only the border.
//
// This runs after Copy To Final and writes the display-encoded scene color, so
// the outline is never tonemapped, upscaled or fed to TAA. The colour is
// written through as authored.
#define WOLF_NO_RW_TEXTURES 1
import "../Common/common.slang";
import "../Common/common_bindless.slang";

// Metal binds the vertex stream at buffer index 0 and resolves registers 10-16
// as SRVs before CBVs, so a pass with its own vertex buffer starts its constant
// buffers above that range. See ScreenSpaceDecalPass, which does the same.
cbuffer OutlineViewParams : register(b17)
{
    row_major float4x4 viewProjection;
    float3 cameraPosition;
    float viewPadding0;
    float2 targetSize;
    float2 depthSize;
    uint depthHandle;
    float depthBias;
    float viewPadding1;
    float viewPadding2;
};

cbuffer OutlineDrawParams : register(b18)
{
    row_major float4x4 world;
    float4 outlineColor;
    float thicknessPixels;
    float drawPadding0;
    float drawPadding1;
    float drawPadding2;
};

// Attribute order must match gbuffer.slang: Metal matches vertex attributes by
// index rather than by semantic name, so a reordering here would silently read
// the wrong stream on Metal while still working on D3D12.
struct VSInput
{
    float3 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float4 tangent : TANGENT;
};

struct VSOutput
{
    float4 position : SV_Position;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;

    float3 worldPosition = mul(float4(input.position, 1.0f), world).xyz - cameraPosition;
    float3 worldNormal = mul(float4(input.normal, 0.0f), world).xyz;

    float4 clipPosition = mul(float4(worldPosition, 1.0f), viewProjection);
    float3 clipNormal = mul(float4(worldNormal, 0.0f), viewProjection).xyz;

    // Extruding in clip space rather than world space keeps the border the same
    // pixel width at every distance. A normal pointing straight at the camera
    // projects to nothing, so it contributes no offset instead of a NaN.
    float2 screenNormal = clipNormal.xy;
    float screenNormalLength = length(screenNormal);
    if (screenNormalLength > 1e-6f)
    {
        float2 offsetNdc = (screenNormal / screenNormalLength) *
            (thicknessPixels * 2.0f / max(targetSize, 1.0f.xx));
        clipPosition.xy += offsetNdc * clipPosition.w;
    }

    output.position = clipPosition;
    return output;
}

float4 fragmentShader(VSOutput input) : SV_Target0
{
    // The scene depth is at render resolution while this pass runs at display
    // resolution, so the sample is rescaled rather than taken one-to-one.
    float2 screenUv = input.position.xy / max(targetSize, 1.0f.xx);
    int2 depthPixel = int2(clamp(screenUv * depthSize, 0.0f.xx, max(depthSize - 1.0f.xx, 0.0f.xx)));
    float sceneDepth = GetTexture(depthHandle).Load(int3(depthPixel, 0)).r;

    // Depth increases with distance here (background is 1.0; see IsBackgroundDepth).
    // Anything at or behind the scene surface is either the hull's interior or a
    // part of the object hidden by other geometry.
    if (input.position.z >= sceneDepth - depthBias)
    {
        discard;
    }

    return outlineColor;
}
