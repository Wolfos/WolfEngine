#define WOLF_NO_RW_TEXTURES 1
import "common_bindless.slang";
import "normal_decode.slang";

cbuffer CameraParams : register(b2)
{
    row_major float4x4 viewProjection;
    float3 cameraPosition;
    float currentJitterPixelsX;
    float3 previousCameraPosition;
    float currentJitterPixelsY;
    row_major float4x4 unjitteredViewProjection;
    row_major float4x4 previousViewProjection;
    float2 currentJitterNdc;
    uint frameSizeX;
    uint frameSizeY;
    row_major float4x4 viewMatrix;
    float nearPlane;
    float farPlane;
    float2 _cameraPadding;
};

cbuffer TerrainDrawParams : register(b3)
{
    row_major float4x4 world;
    uint controlMapHandle;
    uint hasControlMap;
    uint layerSamplerHandle;
    uint controlSamplerHandle;
    uint layerCount;
    float heightBlendSharpness;
    float2 _terrainPadding0;
    uint layer0AlbedoHandle;
    uint layer0NormalHandle;
    uint layer0MetallicRoughnessHandle;
    uint layer0OcclusionHandle;
    uint layer0HeightHandle;
    uint layer0HasHeight;
    uint layer1AlbedoHandle;
    uint layer1NormalHandle;
    uint layer1MetallicRoughnessHandle;
    uint layer1OcclusionHandle;
    uint layer1HeightHandle;
    uint layer1HasHeight;
    uint layer2AlbedoHandle;
    uint layer2NormalHandle;
    uint layer2MetallicRoughnessHandle;
    uint layer2OcclusionHandle;
    uint layer2HeightHandle;
    uint layer2HasHeight;
    uint layer3AlbedoHandle;
    uint layer3NormalHandle;
    uint layer3MetallicRoughnessHandle;
    uint layer3OcclusionHandle;
    uint layer3HeightHandle;
    uint layer3HasHeight;
};

struct VSInput
{
    float4 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float4 tangent : TANGENT;
};

struct VSOutput
{
    float4 position : SV_Position;
    float3 normal : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    float4 currentClip : TEXCOORD3;
    float4 previousClip : TEXCOORD4;
};

struct PSOutput
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 material : SV_Target2;
    float4 emissive : SV_Target3;
    float4 velocity : SV_Target4;
};

struct LayerSample
{
    float4 albedo;
    float3 normal;
    float metallic;
    float roughness;
    float occlusion;
    float height;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    float4 worldPosition = mul(input.position, world);
    float4 previousWorldPosition = worldPosition;
    worldPosition.xyz -= cameraPosition;
    previousWorldPosition.xyz -= previousCameraPosition;
    float4 worldNormal = mul(float4(input.normal, 0.0f), world);
    float3 worldTangent = mul(float4(input.tangent.xyz, 0.0f), world).xyz;
    output.position = mul(worldPosition, viewProjection);
    output.currentClip = mul(worldPosition, unjitteredViewProjection);
    output.previousClip = mul(previousWorldPosition, previousViewProjection);
    output.normal = normalize(worldNormal.xyz);
    output.uv = input.uv;
    output.tangent = float4(normalize(worldTangent), input.tangent.w);
    return output;
}

float2 ClipToPixel(float4 clipPosition, uint2 frameSize)
{
    float invW = abs(clipPosition.w) > 1e-5f ? rcp(clipPosition.w) : 0.0f;
    float2 ndc = clipPosition.xy * invW;
    return float2(
        (ndc.x * 0.5f + 0.5f) * frameSize.x,
        (-ndc.y * 0.5f + 0.5f) * frameSize.y);
}

float ClipToDepth(float4 clipPosition)
{
    float invW = abs(clipPosition.w) > 1e-5f ? rcp(clipPosition.w) : 0.0f;
    return clipPosition.z * invW;
}

float4 SampleControlWeights(float2 uv)
{
    if (hasControlMap == 0)
    {
        return float4(1.0f, 0.0f, 0.0f, 0.0f);
    }

    Texture2D<float4> controlTex = GetTexture(controlMapHandle);
    SamplerState samplerState = GetSampler(controlSamplerHandle);
    return saturate(controlTex.Sample(samplerState, uv));
}

LayerSample SampleLayer(float2 uv, float3 geometryNormal, float4 tangent, uint albedoHandle, uint normalHandle, uint mrHandle, uint occlusionHandle, uint heightHandle, uint hasHeightMap)
{
    SamplerState samplerState = GetSampler(layerSamplerHandle);
    LayerSample sample;
    sample.albedo = GetTexture(albedoHandle).Sample(samplerState, uv);
    float4 mr = GetTexture(mrHandle).Sample(samplerState, uv);
    float4 occ = GetTexture(occlusionHandle).Sample(samplerState, uv);
    float heightValue = 0.5f;
    if (hasHeightMap != 0)
    {
        heightValue = GetTexture(heightHandle).Sample(samplerState, uv).r;
    }

    float3 tangentNormal = DecodeNormalMapSample(GetTexture(normalHandle).Sample(samplerState, uv));
    float3 N = normalize(geometryNormal);
    float3 T = normalize(tangent.xyz);
    float3 B = normalize(cross(N, T) * tangent.w);
    float3x3 TBN = float3x3(T, B, N);
    sample.normal = normalize(mul(tangentNormal, TBN));
    sample.metallic = saturate(mr.b);
    sample.roughness = saturate(mr.g);
    sample.occlusion = saturate(occ.r);
    sample.height = saturate(heightValue);
    return sample;
}

float4 NormalizeWeights(float4 weights)
{
    float sum = weights.x + weights.y + weights.z + weights.w;
    if (sum <= 1e-5f)
    {
        return float4(1.0f, 0.0f, 0.0f, 0.0f);
    }

    return weights / sum;
}

float4 MaskInactiveLayers(float4 weights, uint activeLayerCount)
{
    float4 masked = weights;
    if (activeLayerCount < 4) masked.w = 0.0f;
    if (activeLayerCount < 3) masked.z = 0.0f;
    if (activeLayerCount < 2) masked.y = 0.0f;
    return masked;
}

PSOutput fragmentShader(VSOutput input)
{
    PSOutput output;
    float4 control = MaskInactiveLayers(SampleControlWeights(input.uv), layerCount);
    LayerSample layer0 = SampleLayer(input.uv, input.normal, input.tangent, layer0AlbedoHandle, layer0NormalHandle, layer0MetallicRoughnessHandle, layer0OcclusionHandle, layer0HeightHandle, layer0HasHeight);
    LayerSample layer1 = SampleLayer(input.uv, input.normal, input.tangent, layer1AlbedoHandle, layer1NormalHandle, layer1MetallicRoughnessHandle, layer1OcclusionHandle, layer1HeightHandle, layer1HasHeight);
    LayerSample layer2 = SampleLayer(input.uv, input.normal, input.tangent, layer2AlbedoHandle, layer2NormalHandle, layer2MetallicRoughnessHandle, layer2OcclusionHandle, layer2HeightHandle, layer2HasHeight);
    LayerSample layer3 = SampleLayer(input.uv, input.normal, input.tangent, layer3AlbedoHandle, layer3NormalHandle, layer3MetallicRoughnessHandle, layer3OcclusionHandle, layer3HeightHandle, layer3HasHeight);

    float4 biased = control;
    if (layerCount > 0) biased.x *= pow(max(layer0.height, 0.0001f), max(heightBlendSharpness, 0.0001f));
    if (layerCount > 1) biased.y *= pow(max(layer1.height, 0.0001f), max(heightBlendSharpness, 0.0001f));
    if (layerCount > 2) biased.z *= pow(max(layer2.height, 0.0001f), max(heightBlendSharpness, 0.0001f));
    if (layerCount > 3) biased.w *= pow(max(layer3.height, 0.0001f), max(heightBlendSharpness, 0.0001f));
    float4 weights = NormalizeWeights(biased);

    float4 albedo = layer0.albedo * weights.x +
                    layer1.albedo * weights.y +
                    layer2.albedo * weights.z +
                    layer3.albedo * weights.w;
    float3 normal = normalize(layer0.normal * weights.x +
                              layer1.normal * weights.y +
                              layer2.normal * weights.z +
                              layer3.normal * weights.w);
    float metallic = layer0.metallic * weights.x +
                     layer1.metallic * weights.y +
                     layer2.metallic * weights.z +
                     layer3.metallic * weights.w;
    float roughness = layer0.roughness * weights.x +
                      layer1.roughness * weights.y +
                      layer2.roughness * weights.z +
                      layer3.roughness * weights.w;
    float occlusion = layer0.occlusion * weights.x +
                      layer1.occlusion * weights.y +
                      layer2.occlusion * weights.z +
                      layer3.occlusion * weights.w;

    output.albedo = albedo;
    output.normal = float4(normal * 0.5f + 0.5f, 1.0f);
    output.material = float4(saturate(metallic), saturate(roughness), saturate(occlusion), 0.0f);
    output.emissive = float4(0.0f, 0.0f, 0.0f, 1.0f);
    uint2 frameSize = uint2(max(frameSizeX, 1u), max(frameSizeY, 1u));
    float2 currentPixel = ClipToPixel(input.currentClip, frameSize);
    float2 previousPixel = ClipToPixel(input.previousClip, frameSize);
    float previousDepth = ClipToDepth(input.previousClip);
    output.velocity = float4(previousPixel - currentPixel, previousDepth, 1.0f);
    return output;
}
