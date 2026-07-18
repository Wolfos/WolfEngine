#define WOLF_NO_RW_TEXTURES 1
import "common.slang";
import "common_bindless.slang";
import "draw_data.slang";
import "normal_decode.slang";

static const uint DECAL_CHANNEL_ALBEDO = 1u << 0;
static const uint DECAL_CHANNEL_NORMAL = 1u << 1;
static const uint DECAL_CHANNEL_MATERIAL = 1u << 2;
static const uint DECAL_CHANNEL_EMISSIVE = 1u << 3;

StructuredBuffer<GpuDecalProjectorData> g_Decals : register(t16);

cbuffer BindlessHandles : register(b17)
{
    uint sourceAlbedoHandle;
    uint sourceNormalHandle;
    uint sourceMaterialHandle;
    uint sourceEmissiveHandle;
    uint depthHandle;
};

cbuffer CameraParams : register(b18)
{
    row_major float4x4 viewProjection;
    row_major float4x4 invViewProjection;
    uint frameSizeX;
    uint frameSizeY;
    float2 _cameraPadding;
};

cbuffer DrawParams : register(b19)
{
    uint decalIndex;
    float3 _drawPadding;
};

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
    float4 clipPosition : TEXCOORD0;
};

float3 ReconstructWorldPosition(uint2 pixel, uint2 size, float depth, float4x4 inverseViewProjection)
{
    float2 ndc;
    ndc.x = (float(pixel.x) + 0.5f) / float(size.x) * 2.0f - 1.0f;
    ndc.y = -((float(pixel.y) + 0.5f) / float(size.y) * 2.0f - 1.0f);

    float4 clip = float4(ndc, depth, 1.0f);
    float4 worldPosH = mul(clip, inverseViewProjection);
    return worldPosH.xyz / max(worldPosH.w, 1e-6f);
}

float3 DecodeStoredNormal(float4 encodedNormal)
{
    return normalize(encodedNormal.xyz * 2.0f - 1.0f);
}

float3 EncodeStoredNormal(float3 normal)
{
    return normalize(normal) * 0.5f + 0.5f;
}

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    GpuDecalProjectorData decal = g_Decals[decalIndex];
    float4 worldPosition = mul(float4(input.position, 1.0f), decal.localToWorld);
    output.position = mul(worldPosition, viewProjection);
    output.clipPosition = output.position;
    return output;
}

struct PSOutput
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 material : SV_Target2;
    float4 emissive : SV_Target3;
};

PSOutput fragmentShader(VSOutput input)
{
    PSOutput output;

    uint2 framebufferSize = uint2(max(frameSizeX, 1u), max(frameSizeY, 1u));
    uint2 pixel = uint2(clamp(input.position.xy, float2(0.0f, 0.0f), float2(framebufferSize - 1u)));
    float2 screenUv = (float2(pixel) + 0.5f) / float2(framebufferSize);

    Texture2D<float4> depthTexture = GetTexture(depthHandle);
    float depth = depthTexture.Load(int3(pixel, 0)).r;
    if (IsBackgroundDepth(depth))
    {
        discard;
    }

    GpuDecalProjectorData decal = g_Decals[decalIndex];
    float3 worldPosition = ReconstructWorldPosition(pixel, framebufferSize, depth, invViewProjection);
    float4 localPosition = mul(float4(worldPosition, 1.0f), decal.worldToLocal);
    if (abs(localPosition.x) > 0.5f || abs(localPosition.y) > 0.5f || abs(localPosition.z) > 0.5f)
    {
        discard;
    }

    float2 decalUv = float2(localPosition.x + 0.5f, 0.5f - localPosition.y);
    decalUv = decalUv * decal.uvScaleOffset.xy + decal.uvScaleOffset.zw;

    Texture2D<float4> sourceAlbedo = GetTexture(sourceAlbedoHandle);
    Texture2D<float4> sourceNormal = GetTexture(sourceNormalHandle);
    Texture2D<float4> sourceMaterial = GetTexture(sourceMaterialHandle);
    Texture2D<float4> sourceEmissive = GetTexture(sourceEmissiveHandle);
    SamplerState samplerState = GetSampler(decal.samplerHandle);

    float4 existingAlbedo = sourceAlbedo.SampleLevel(samplerState, screenUv, 0.0f);
    float4 existingNormal = sourceNormal.SampleLevel(samplerState, screenUv, 0.0f);
    float4 existingMaterial = sourceMaterial.SampleLevel(samplerState, screenUv, 0.0f);
    float4 existingEmissive = sourceEmissive.SampleLevel(samplerState, screenUv, 0.0f);

    float4 albedoSample = GetTexture(decal.albedoHandle).SampleLevel(samplerState, decalUv, 0.0f);
    float4 normalSample = GetTexture(decal.normalHandle).SampleLevel(samplerState, decalUv, 0.0f);
    float4 materialSample = GetTexture(decal.materialHandle).SampleLevel(samplerState, decalUv, 0.0f);
    float4 emissiveSample = GetTexture(decal.emissiveHandle).SampleLevel(samplerState, decalUv, 0.0f);

    output.albedo = existingAlbedo;
    output.normal = existingNormal;
    output.material = existingMaterial;
    output.emissive = existingEmissive;

    if ((decal.channelMask & DECAL_CHANNEL_ALBEDO) != 0u)
    {
        float albedoBlend = saturate(albedoSample.a * decal.opacities.x);
        float3 decalColor = albedoSample.rgb * decal.tint.rgb;
        output.albedo = float4(lerp(existingAlbedo.rgb, decalColor, albedoBlend), existingAlbedo.a);
    }

    if ((decal.channelMask & DECAL_CHANNEL_NORMAL) != 0u)
    {
        float normalBlend = saturate(normalSample.a * decal.opacities.y);
        float3 existingNormalWs = DecodeStoredNormal(existingNormal);
        float3 decalNormalLocal = DecodeNormalMapSample(normalSample);
        float3 decalNormalWs = normalize(mul(float4(decalNormalLocal, 0.0f), decal.localToWorld).xyz);
        output.normal = float4(EncodeStoredNormal(normalize(lerp(existingNormalWs, decalNormalWs, normalBlend))), existingNormal.w);
    }

    if ((decal.channelMask & DECAL_CHANNEL_MATERIAL) != 0u)
    {
        float materialBlend = saturate(materialSample.a * decal.opacities.z);
        float3 decalMaterial = saturate(materialSample.rgb * decal.materialFactorsEmissive.rgb);
        output.material = float4(lerp(existingMaterial.rgb, decalMaterial, materialBlend), existingMaterial.a);
    }

    if ((decal.channelMask & DECAL_CHANNEL_EMISSIVE) != 0u)
    {
        float emissiveBlend = saturate(emissiveSample.a * decal.opacities.w);
        float3 decalEmissive = emissiveSample.rgb * decal.materialFactorsEmissive.w * decal.tint.rgb;
        output.emissive = float4(lerp(existingEmissive.rgb, decalEmissive, emissiveBlend), existingEmissive.a);
    }

    return output;
}
