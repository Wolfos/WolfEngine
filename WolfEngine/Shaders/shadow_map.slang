#define WOLF_NO_RW_TEXTURES 1
import "common_bindless.slang";
import "draw_data.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535

StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t10);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t11);
StructuredBuffer<GpuDrawArgs> g_DrawArgsTable : register(t12);
StructuredBuffer<uint> g_MaterialGenerations : register(t13);
StructuredBuffer<GpuTerrainMaterialData> g_TerrainMaterialTable : register(t14);

cbuffer CameraParams : register(b16)
{
    row_major float4x4 viewProjection0;
    row_major float4x4 viewProjection1;
    row_major float4x4 viewProjection2;
    float3 cameraPosition;
    float padding;
};

#ifndef WOLF_SHADOW_CASCADE_INDEX
#define WOLF_SHADOW_CASCADE_INDEX 0
#endif

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
    float2 uv : TEXCOORD0;
    uint instanceId : TEXCOORD1;
};

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

float2 GetTerrainUv(float2 baseUv, GpuInstanceData instanceData)
{
    return instanceData.terrainHeightmapUvScaleOffset.zw + baseUv * instanceData.terrainHeightmapUvScaleOffset.xy;
}

float CubicHermite(float a, float b, float c, float d, float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return b + 0.5f * t * (c - a + t * (2.0f * a - 5.0f * b + 4.0f * c - d + t * (3.0f * (b - c) + d - a)));
}

float SampleTerrainHeight(float2 uv, GpuTerrainMaterialData terrainData)
{
    Texture2D<float4> heightmap = GetTexture(terrainData.heightmapHandle);
    uint width = 1;
    uint height = 1;
    heightmap.GetDimensions(width, height);
    if (width <= 1 || height <= 1)
    {
        return heightmap.Load(int3(0, 0, 0)).r * terrainData.heightScale;
    }

    float2 samplePosition = saturate(uv) * float2(width - 1, height - 1);
    int2 baseTexel = int2(floor(samplePosition));
    float2 fraction = samplePosition - float2(baseTexel);
    float rows[4];
    [unroll]
    for (int row = 0; row < 4; row++)
    {
        int y = clamp(baseTexel.y + row - 1, 0, int(height) - 1);
        float samples[4];
        [unroll]
        for (int column = 0; column < 4; column++)
        {
            int x = clamp(baseTexel.x + column - 1, 0, int(width) - 1);
            samples[column] = heightmap.Load(int3(x, y, 0)).r;
        }

        rows[row] = CubicHermite(samples[0], samples[1], samples[2], samples[3], fraction.x);
    }

    return saturate(CubicHermite(rows[0], rows[1], rows[2], rows[3], fraction.y)) * terrainData.heightScale;
}

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    GpuDrawArgs drawArgs = g_DrawArgsTable[0];
    if (drawArgs.instanceCount == 0 || drawArgs.startInstance == 0 || drawArgs.startInstance >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.position = float4(2.0f, 2.0f, 2.0f, 1.0f);
        output.uv = float2(0.0f, 0.0f);
        output.instanceId = 0;
        return output;
    }

    uint instanceId = drawArgs.startInstance;
    GpuInstanceData instanceData = g_InstanceTable[instanceId];
    uint materialIndex = ResolveMaterialIndex(instanceData.materialHandle);
    float2 uv = input.uv;
    float3 localPosition = input.position.xyz;
    if (instanceData.drawKind == 2)
    {
        GpuTerrainMaterialData terrainData = g_TerrainMaterialTable[materialIndex];
        uv = GetTerrainUv(input.uv, instanceData);
        localPosition.x = instanceData.terrainChunkOriginSize.x + input.position.x * instanceData.terrainChunkOriginSize.z;
        localPosition.y = SampleTerrainHeight(uv, terrainData) + input.position.y;
        localPosition.z = instanceData.terrainChunkOriginSize.y + input.position.z * instanceData.terrainChunkOriginSize.w;
    }

    float4 worldPosition = mul(float4(localPosition, 1.0f), instanceData.world);
    worldPosition.xyz -= cameraPosition;

    row_major float4x4 viewProjection = viewProjection0;
#if WOLF_SHADOW_CASCADE_INDEX == 1
    viewProjection = viewProjection1;
#elif WOLF_SHADOW_CASCADE_INDEX == 2
    viewProjection = viewProjection2;
#endif
    output.position = mul(worldPosition, viewProjection);
    output.uv = uv;
    output.instanceId = instanceId;
    return output;
}

void fragmentShader(VSOutput input)
{
#ifdef WOLF_ALPHA_CLIP
    if (input.instanceId >= WOLF_MAX_INSTANCE_COUNT)
    {
        discard;
    }

    GpuInstanceData instanceData = g_InstanceTable[input.instanceId];
    uint materialIndex = 0;
    uint materialHandle = instanceData.materialHandle;
    if (IsPackedHandleValid(materialHandle))
    {
        uint candidateIndex = GetHandleIndex(materialHandle);
        uint candidateGeneration = GetHandleGeneration(materialHandle);
        if (candidateIndex < WOLF_MAX_MATERIAL_COUNT &&
            g_MaterialGenerations[candidateIndex] == candidateGeneration)
        {
            materialIndex = candidateIndex;
        }
    }

    GpuMaterialData materialData = g_MaterialTable[materialIndex];
    SamplerState sampler = GetSampler(materialData.samplerHandle);
    Texture2D<float4> albedoTex = GetTexture(materialData.albedoHandle);
    float alpha = albedoTex.Sample(sampler, input.uv).a * materialData.baseColor.a;
    if (alpha < materialData.metallicRoughness.z)
    {
        discard;
    }
#endif
}
