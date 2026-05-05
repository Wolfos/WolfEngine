#define WOLF_NO_RW_TEXTURES 1
import "common_bindless.slang";
import "draw_data.slang";
import "normal_decode.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535

StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t10);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t11);
StructuredBuffer<GpuDrawArgs> g_DrawArgsTable : register(t12);
StructuredBuffer<uint> g_MaterialGenerations : register(t13);
StructuredBuffer<GpuTerrainMaterialData> g_TerrainMaterialTable : register(t14);
StructuredBuffer<GpuTerrainLayerData> g_TerrainLayerTable : register(t15);

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

struct VSInput
{
    float4 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float4 tangent : TANGENT;
    uint instanceId : SV_InstanceID;
};

struct VSOutput
{
    float4 position : SV_Position;
    float3 normal : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    uint instanceId : TEXCOORD3;
    float4 currentClip : TEXCOORD4;
    float4 previousClip : TEXCOORD5;
    float3 worldPosition : TEXCOORD6;
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

float2 GetTerrainUv(float2 baseUv, GpuInstanceData instanceData)
{
    return instanceData.terrainHeightmapUvScaleOffset.zw + baseUv * instanceData.terrainHeightmapUvScaleOffset.xy;
}

float2 GetTerrainWorldSize(GpuInstanceData instanceData)
{
    float sizeX = abs(instanceData.terrainHeightmapUvScaleOffset.x) > 1e-5f
        ? instanceData.terrainChunkOriginSize.z / instanceData.terrainHeightmapUvScaleOffset.x
        : instanceData.terrainChunkOriginSize.z;
    float sizeZ = abs(instanceData.terrainHeightmapUvScaleOffset.y) > 1e-5f
        ? instanceData.terrainChunkOriginSize.w / instanceData.terrainHeightmapUvScaleOffset.y
        : instanceData.terrainChunkOriginSize.w;
    return float2(max(sizeX, 0.001f), max(sizeZ, 0.001f));
}

float SampleTerrainHeight(float2 uv, GpuTerrainMaterialData terrainData)
{
    Texture2D<float4> heightmap = GetTexture(terrainData.heightmapHandle);
    SamplerState samplerState = GetSampler(terrainData.heightmapSamplerHandle);
    return heightmap.SampleLevel(samplerState, saturate(uv), 0.0f).r * terrainData.heightScale;
}

float3 ComputeTerrainLocalNormal(float2 uv, GpuInstanceData instanceData, GpuTerrainMaterialData terrainData)
{
    Texture2D<float4> heightmap = GetTexture(terrainData.heightmapHandle);
    uint width = 1;
    uint height = 1;
    heightmap.GetDimensions(width, height);
    float2 texel = float2(
        width > 1 ? 1.0f / (width - 1) : 0.0f,
        height > 1 ? 1.0f / (height - 1) : 0.0f);
    float left = SampleTerrainHeight(uv - float2(texel.x, 0.0f), terrainData);
    float right = SampleTerrainHeight(uv + float2(texel.x, 0.0f), terrainData);
    float down = SampleTerrainHeight(uv - float2(0.0f, texel.y), terrainData);
    float up = SampleTerrainHeight(uv + float2(0.0f, texel.y), terrainData);
    float2 terrainWorldSize = GetTerrainWorldSize(instanceData);
    float spacingX = terrainWorldSize.x / max(width - 1, 1);
    float spacingZ = terrainWorldSize.y / max(height - 1, 1);
    float3 tangentX = float3(max(spacingX * 2.0f, 0.001f), right - left, 0.0f);
    float3 tangentZ = float3(0.0f, up - down, max(spacingZ * 2.0f, 0.001f));
    return normalize(cross(tangentZ, tangentX));
}

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    GpuDrawArgs drawArgs = g_DrawArgsTable[0];
    if (drawArgs.instanceCount == 0 || drawArgs.startInstance == 0 || drawArgs.startInstance >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.position = float4(2.0f, 2.0f, 2.0f, 1.0f);
        output.normal = float3(0.0f, 1.0f, 0.0f);
        output.uv = float2(0.0f, 0.0f);
        output.tangent = float4(1.0f, 0.0f, 0.0f, 1.0f);
        output.instanceId = 0;
        output.currentClip = output.position;
        output.previousClip = output.position;
        output.worldPosition = float3(0.0f, 0.0f, 0.0f);
        return output;
    }

    uint instanceId = drawArgs.startInstance;
    GpuInstanceData instanceData = g_InstanceTable[instanceId];
    uint materialIndex = ResolveMaterialIndex(instanceData.materialHandle);
    GpuTerrainMaterialData terrainData = g_TerrainMaterialTable[materialIndex];
    float2 terrainUv = GetTerrainUv(input.uv, instanceData);
    float3 localPosition;
    localPosition.x = instanceData.terrainChunkOriginSize.x + input.position.x * instanceData.terrainChunkOriginSize.z;
    localPosition.y = SampleTerrainHeight(terrainUv, terrainData) + input.position.y;
    localPosition.z = instanceData.terrainChunkOriginSize.y + input.position.z * instanceData.terrainChunkOriginSize.w;
    float3 localNormal = ComputeTerrainLocalNormal(terrainUv, instanceData, terrainData);
    float3 localTangent = float3(1.0f, 0.0f, 0.0f);
    float4 absoluteWorldPosition = mul(float4(localPosition, 1.0f), instanceData.world);
    float4 worldPosition = absoluteWorldPosition;
    float4 previousWorldPosition = mul(float4(localPosition, 1.0f), instanceData.previousWorld);
    worldPosition.xyz -= cameraPosition;
    previousWorldPosition.xyz -= previousCameraPosition;
    float4 worldNormal = mul(float4(localNormal, 0.0f), instanceData.world);
    float3 worldTangent = mul(float4(localTangent, 0.0f), instanceData.world).xyz;

    output.position = mul(worldPosition, viewProjection);
    output.currentClip = mul(worldPosition, unjitteredViewProjection);
    output.previousClip = mul(previousWorldPosition, previousViewProjection);
    output.normal = normalize(worldNormal.xyz);
    output.uv = terrainUv;
    output.tangent = float4(normalize(worldTangent), input.tangent.w);
    output.instanceId = instanceId;
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

uint ResolveMaterialIndex(uint materialHandle)
{
    if (IsPackedHandleValid(materialHandle))
    {
        uint candidateIndex = GetHandleIndex(materialHandle);
        uint candidateGeneration = GetHandleGeneration(materialHandle);
        if (candidateIndex < WOLF_MAX_MATERIAL_COUNT &&
            g_MaterialGenerations[candidateIndex] == candidateGeneration)
        {
            return candidateIndex;
        }
    }

    return 0;
}

struct TerrainLayerBlend
{
    uint4 indices;
    float4 weights;
};

TerrainLayerBlend SampleTerrainLayerBlend(float2 uv, GpuTerrainMaterialData terrainData)
{
    TerrainLayerBlend blend;
    blend.indices = uint4(0, 0, 0, 0);
    blend.weights = float4(1.0f, 0.0f, 0.0f, 0.0f);

    if (terrainData.hasLayerMaps == 0)
    {
        return blend;
    }

    Texture2D<float4> indexMap = GetTexture(terrainData.layerIndexMapHandle);
    uint indexWidth = 1;
    uint indexHeight = 1;
    indexMap.GetDimensions(indexWidth, indexHeight);
    uint2 indexTexel = uint2(
        min((uint)floor(saturate(uv.x) * indexWidth), max(indexWidth, 1u) - 1u),
        min((uint)floor(saturate(uv.y) * indexHeight), max(indexHeight, 1u) - 1u));
    float4 indexSample = indexMap.Load(int3(indexTexel, 0));
    blend.indices = uint4(round(saturate(indexSample) * 255.0f));
    SamplerState samplerState = GetSampler(terrainData.layerMapSamplerHandle);
    blend.weights = saturate(GetTexture(terrainData.layerWeightMapHandle).SampleLevel(samplerState, uv, 0.0f));
    return blend;
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

LayerSample SampleTriplanarLayer(
    float3 worldPosition,
    float3 geometryNormal,
    uint albedoHandle,
    uint normalHandle,
    uint ormHandle,
    uint heightHandle,
    uint hasHeightMap,
    float layerScale,
    GpuTerrainMaterialData terrainData)
{
    SamplerState samplerState = GetSampler(terrainData.layerSamplerHandle);
    LayerSample sample;
    sample.albedo = SampleTriplanarFloat4(albedoHandle, samplerState, worldPosition, geometryNormal, layerScale);
    float4 orm = SampleTriplanarFloat4(ormHandle, samplerState, worldPosition, geometryNormal, layerScale);
    float heightValue = 0.5f;
    if (hasHeightMap != 0)
    {
        heightValue = SampleTriplanarFloat4(heightHandle, samplerState, worldPosition, geometryNormal, layerScale).r;
    }

    sample.normal = DecodeTriplanarNormal(normalHandle, samplerState, worldPosition, geometryNormal, layerScale);
    sample.occlusion = saturate(orm.r);
    sample.roughness = saturate(orm.g);
    sample.metallic = saturate(orm.b);
    sample.height = saturate(heightValue);
    return sample;
}

PSOutput fragmentShader(VSOutput input)
{
    PSOutput output;
    if (input.instanceId >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.albedo = float4(1.0f, 0.0f, 1.0f, 1.0f);
        output.normal = float4(0.5f, 0.5f, 1.0f, 1.0f);
        output.material = float4(0.0f, 1.0f, 1.0f, 1.0f);
        output.emissive = float4(0.0f, 0.0f, 0.0f, 1.0f);
        output.velocity = float4(0.0f, 0.0f, 1.0f, 1.0f);
        return output;
    }

    GpuInstanceData instanceData = g_InstanceTable[input.instanceId];
    uint materialIndex = ResolveMaterialIndex(instanceData.materialHandle);
    GpuTerrainMaterialData terrainData = g_TerrainMaterialTable[materialIndex];

    TerrainLayerBlend blend = SampleTerrainLayerBlend(input.uv, terrainData);
    blend.weights = MaskInactiveLayers(blend.weights, terrainData.layerCount);
    LayerSample layerSamples[4];
    [unroll]
    for (uint i = 0; i < 4; i++)
    {
        layerSamples[i] = (LayerSample)0;
        uint layerIndex = min(blend.indices[i], max(terrainData.layerCount, 1u) - 1u);
        if (blend.weights[i] <= 0.0f || layerIndex >= terrainData.layerCount)
        {
            continue;
        }

        GpuTerrainLayerData layerData = g_TerrainLayerTable[terrainData.layerStart + layerIndex];
        layerSamples[i] = SampleTriplanarLayer(
            input.worldPosition,
            input.normal,
            layerData.albedoHandle,
            layerData.normalHandle,
            layerData.ormHandle,
            layerData.heightHandle,
            layerData.hasHeight,
            layerData.scale,
            terrainData);
    }

    float4 biased = blend.weights;

    biased.x *= pow(max(layerSamples[0].height, 0.0001f), max(terrainData.heightBlendSharpness, 0.0001f));
    biased.y *= pow(max(layerSamples[1].height, 0.0001f), max(terrainData.heightBlendSharpness, 0.0001f));
    biased.z *= pow(max(layerSamples[2].height, 0.0001f), max(terrainData.heightBlendSharpness, 0.0001f));
    biased.w *= pow(max(layerSamples[3].height, 0.0001f), max(terrainData.heightBlendSharpness, 0.0001f));
    float4 weights = NormalizeWeights(biased);

    float4 albedo = layerSamples[0].albedo * weights.x +
                    layerSamples[1].albedo * weights.y +
                    layerSamples[2].albedo * weights.z +
                    layerSamples[3].albedo * weights.w;
    float3 normal = normalize(layerSamples[0].normal * weights.x +
                              layerSamples[1].normal * weights.y +
                              layerSamples[2].normal * weights.z +
                              layerSamples[3].normal * weights.w);
    float metallic = saturate(layerSamples[0].metallic * weights.x +
                              layerSamples[1].metallic * weights.y +
                              layerSamples[2].metallic * weights.z +
                              layerSamples[3].metallic * weights.w);
    float roughness = saturate(layerSamples[0].roughness * weights.x +
                               layerSamples[1].roughness * weights.y +
                               layerSamples[2].roughness * weights.z +
                               layerSamples[3].roughness * weights.w);
    float occlusion = saturate(layerSamples[0].occlusion * weights.x +
                               layerSamples[1].occlusion * weights.y +
                               layerSamples[2].occlusion * weights.z +
                               layerSamples[3].occlusion * weights.w);

    output.albedo = albedo;
    output.normal = float4(normal * 0.5f + 0.5f, 1.0f);
    output.material = float4(occlusion, roughness, metallic, 0.0f);
    output.emissive = float4(0.0f, 0.0f, 0.0f, 1.0f);

    uint2 frameSize = uint2(max(frameSizeX, 1u), max(frameSizeY, 1u));
    float2 currentPixel = ClipToPixel(input.currentClip, frameSize);
    float2 previousPixel = ClipToPixel(input.previousClip, frameSize);
    float previousDepth = ClipToDepth(input.previousClip);
    output.velocity = float4(previousPixel - currentPixel, previousDepth, 1.0f);
    return output;
}
