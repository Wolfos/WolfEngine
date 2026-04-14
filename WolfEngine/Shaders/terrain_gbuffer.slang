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
    float layer0Scale;
    float _layer0Padding0;
    float _layer0Padding1;
    uint layer1AlbedoHandle;
    uint layer1NormalHandle;
    uint layer1MetallicRoughnessHandle;
    uint layer1OcclusionHandle;
    uint layer1HeightHandle;
    uint layer1HasHeight;
    float layer1Scale;
    float _layer1Padding0;
    float _layer1Padding1;
    uint layer2AlbedoHandle;
    uint layer2NormalHandle;
    uint layer2MetallicRoughnessHandle;
    uint layer2OcclusionHandle;
    uint layer2HeightHandle;
    uint layer2HasHeight;
    float layer2Scale;
    float _layer2Padding0;
    float _layer2Padding1;
    uint layer3AlbedoHandle;
    uint layer3NormalHandle;
    uint layer3MetallicRoughnessHandle;
    uint layer3OcclusionHandle;
    uint layer3HeightHandle;
    uint layer3HasHeight;
    float layer3Scale;
    float _layer3Padding0;
    float _layer3Padding1;
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
    float3 worldPosition : TEXCOORD3;
    float4 currentClip : TEXCOORD4;
    float4 previousClip : TEXCOORD5;
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
    float4 absoluteWorldPosition = mul(input.position, world);
    float4 worldPosition = absoluteWorldPosition;
    float4 previousWorldPosition = absoluteWorldPosition;
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
    output.worldPosition = absoluteWorldPosition.xyz;
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

float3 GetTriplanarWeights(float3 worldNormal)
{
    float3 weights = pow(abs(normalize(worldNormal)), 4.0f);
    float sum = weights.x + weights.y + weights.z;
    return sum > 1e-5f ? weights / sum : float3(0.0f, 1.0f, 0.0f);
}

float2 GetTriplanarUvX(float3 worldPosition, float layerScale)
{
    return worldPosition.zy / max(layerScale, 0.001f);
}

float2 GetTriplanarUvY(float3 worldPosition, float layerScale)
{
    return worldPosition.xz / max(layerScale, 0.001f);
}

float2 GetTriplanarUvZ(float3 worldPosition, float layerScale)
{
    return worldPosition.xy / max(layerScale, 0.001f);
}

float4 SampleTriplanarFloat4(uint textureHandle, SamplerState samplerState, float3 worldPosition, float3 worldNormal, float layerScale)
{
    float3 weights = GetTriplanarWeights(worldNormal);
    float4 sampleX = GetTexture(textureHandle).Sample(samplerState, GetTriplanarUvX(worldPosition, layerScale));
    float4 sampleY = GetTexture(textureHandle).Sample(samplerState, GetTriplanarUvY(worldPosition, layerScale));
    float4 sampleZ = GetTexture(textureHandle).Sample(samplerState, GetTriplanarUvZ(worldPosition, layerScale));
    return sampleX * weights.x + sampleY * weights.y + sampleZ * weights.z;
}

float3 DecodeTriplanarNormal(uint normalHandle, SamplerState samplerState, float3 worldPosition, float3 worldNormal, float layerScale)
{
    float3 weights = GetTriplanarWeights(worldNormal);
    float3 normalX = DecodeNormalMapSample(GetTexture(normalHandle).Sample(samplerState, GetTriplanarUvX(worldPosition, layerScale)));
    float3 normalY = DecodeNormalMapSample(GetTexture(normalHandle).Sample(samplerState, GetTriplanarUvY(worldPosition, layerScale)));
    float3 normalZ = DecodeNormalMapSample(GetTexture(normalHandle).Sample(samplerState, GetTriplanarUvZ(worldPosition, layerScale)));

    float3 worldNormalX = normalize(float3(normalX.z, normalX.y, normalX.x));
    float3 worldNormalY = normalize(float3(normalY.x, normalY.z, normalY.y));
    float3 worldNormalZ = normalize(float3(normalZ.x, normalZ.y, normalZ.z));
    return normalize(worldNormalX * weights.x + worldNormalY * weights.y + worldNormalZ * weights.z);
}

LayerSample SampleTriplanarLayer(float3 worldPosition, float3 geometryNormal, uint albedoHandle, uint normalHandle, uint mrHandle, uint occlusionHandle, uint heightHandle, uint hasHeightMap, float layerScale)
{
    SamplerState samplerState = GetSampler(layerSamplerHandle);
    LayerSample sample;
    sample.albedo = SampleTriplanarFloat4(albedoHandle, samplerState, worldPosition, geometryNormal, layerScale);
    float4 mr = SampleTriplanarFloat4(mrHandle, samplerState, worldPosition, geometryNormal, layerScale);
    float4 occ = SampleTriplanarFloat4(occlusionHandle, samplerState, worldPosition, geometryNormal, layerScale);
    float heightValue = 0.5f;
    if (hasHeightMap != 0)
    {
        heightValue = SampleTriplanarFloat4(heightHandle, samplerState, worldPosition, geometryNormal, layerScale).r;
    }

    sample.normal = DecodeTriplanarNormal(normalHandle, samplerState, worldPosition, geometryNormal, layerScale);
    sample.metallic = saturate(mr.b);
    sample.roughness = saturate(mr.g);
    sample.occlusion = saturate(occ.r);
    sample.height = saturate(heightValue);
    return sample;
}

PSOutput fragmentShader(VSOutput input)
{
    PSOutput output;
    float4 control = MaskInactiveLayers(SampleControlWeights(input.uv), layerCount);
    LayerSample layer0 = SampleTriplanarLayer(input.worldPosition, input.normal, layer0AlbedoHandle, layer0NormalHandle, layer0MetallicRoughnessHandle, layer0OcclusionHandle, layer0HeightHandle, layer0HasHeight, layer0Scale);
    LayerSample layer1 = SampleTriplanarLayer(input.worldPosition, input.normal, layer1AlbedoHandle, layer1NormalHandle, layer1MetallicRoughnessHandle, layer1OcclusionHandle, layer1HeightHandle, layer1HasHeight, layer1Scale);
    LayerSample layer2 = SampleTriplanarLayer(input.worldPosition, input.normal, layer2AlbedoHandle, layer2NormalHandle, layer2MetallicRoughnessHandle, layer2OcclusionHandle, layer2HeightHandle, layer2HasHeight, layer2Scale);
    LayerSample layer3 = SampleTriplanarLayer(input.worldPosition, input.normal, layer3AlbedoHandle, layer3NormalHandle, layer3MetallicRoughnessHandle, layer3OcclusionHandle, layer3HeightHandle, layer3HasHeight, layer3Scale);

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
