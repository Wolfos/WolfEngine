import "../Common/draw_data.slang";

// Terrain layer resolution shared by the g-buffer pass and the DDGI trace. Both turn layer-map
// texels into the same four-slot blend and they have to agree: when they drift apart a slope is lit
// as rock while its bounce lighting is gathered from sand. The layer table and the layer maps are
// parameters because each pass binds them to its own registers.

struct TerrainLayerBlend
{
    uint4 indices;
    float4 weights;
};

void AddTerrainLayerContribution(inout TerrainLayerBlend blend, uint layerIndex, float contribution)
{
    if (contribution <= 0.0f)
    {
        return;
    }

    [unroll]
    for (uint slot = 0u; slot < 4u; slot++)
    {
        if (blend.weights[slot] > 0.0f && blend.indices[slot] == layerIndex)
        {
            blend.weights[slot] += contribution;
            return;
        }
    }

    uint minSlot = 0u;
    float minWeight = blend.weights.x;
    [unroll]
    for (uint slot = 0u; slot < 4u; slot++)
    {
        if (blend.weights[slot] <= 0.0f)
        {
            blend.indices[slot] = layerIndex;
            blend.weights[slot] = contribution;
            return;
        }

        if (blend.weights[slot] < minWeight)
        {
            minWeight = blend.weights[slot];
            minSlot = slot;
        }
    }

    // Only four layers fit; the weakest one loses its slot to anything stronger.
    if (contribution > minWeight)
    {
        blend.indices[minSlot] = layerIndex;
        blend.weights[minSlot] = contribution;
    }
}

// An auto layer fades in across a band centred on its minimum slope instead of switching at the
// exact threshold, so the transition reads like a hand-painted stroke and leaves neighbouring layers
// weighted for the height blend the callers apply.
float ComputeTerrainAutoLayerActivation(float slopeDegrees, float minimumSlopeDegrees, float blendDegrees)
{
    if (minimumSlopeDegrees <= 0.0f)
    {
        return 1.0f;
    }

    if (blendDegrees <= 1e-4f)
    {
        return slopeDegrees >= minimumSlopeDegrees ? 1.0f : 0.0f;
    }

    return smoothstep(minimumSlopeDegrees - blendDegrees, minimumSlopeDegrees + blendDegrees, slopeDegrees);
}

void AddTerrainAutoLayerContributions(
    inout TerrainLayerBlend blend,
    StructuredBuffer<GpuTerrainLayerData> layerTable,
    float contribution,
    float3 terrainNormal,
    GpuTerrainMaterialData terrainData)
{
    uint layerCount = max(terrainData.layerCount, 1u);
    float slopeDegrees = degrees(acos(saturate(normalize(terrainNormal).y)));
    float blendDegrees = max(terrainData.autoMaterialBlendDegrees, 0.0f);

    uint isAuto[4];
    float minimumSlopes[4];
    float activations[4];
    [unroll]
    for (uint layerIndex = 0u; layerIndex < 4u; layerIndex++)
    {
        isAuto[layerIndex] = 0u;
        minimumSlopes[layerIndex] = 0.0f;
        activations[layerIndex] = 0.0f;
        if (layerIndex >= layerCount)
        {
            continue;
        }

        GpuTerrainLayerData layer = layerTable[terrainData.layerStart + layerIndex];
        if (layer.autoMaterial == 0u)
        {
            continue;
        }

        isAuto[layerIndex] = 1u;
        minimumSlopes[layerIndex] = layer.useMinimumSlope != 0u ? clamp(layer.minimumSlopeDegrees, 0.0f, 90.0f) : 0.0f;
        activations[layerIndex] = ComputeTerrainAutoLayerActivation(slopeDegrees, minimumSlopes[layerIndex], blendDegrees);
    }

    // Composite steepest-first: each layer only keeps the coverage the steeper layers leave behind,
    // which sums to one without having to sort the layers by threshold.
    float assigned = 0.0f;
    [unroll]
    for (uint layerIndex = 0u; layerIndex < 4u; layerIndex++)
    {
        if (isAuto[layerIndex] == 0u)
        {
            continue;
        }

        float layerWeight = activations[layerIndex];
        [unroll]
        for (uint otherIndex = 0u; otherIndex < 4u; otherIndex++)
        {
            if (otherIndex == layerIndex || isAuto[otherIndex] == 0u)
            {
                continue;
            }

            if (minimumSlopes[otherIndex] > minimumSlopes[layerIndex])
            {
                layerWeight *= 1.0f - activations[otherIndex];
            }
            else if (minimumSlopes[otherIndex] == minimumSlopes[layerIndex] && otherIndex < layerIndex)
            {
                // Layers sharing a threshold cannot be ordered, so the first one still wins outright.
                layerWeight = 0.0f;
            }
        }

        AddTerrainLayerContribution(blend, layerIndex, layerWeight * contribution);
        assigned += layerWeight;
    }

    // Slopes below every auto threshold, and layer sets without any auto layer at all, fall back to
    // layer zero.
    float residual = saturate(1.0f - assigned);
    AddTerrainLayerContribution(blend, 0u, residual * contribution);
}

// Layer-map texels store "layer index + 1" so that zero can mean "let the slope rules decide".
void AddResolvedTerrainLayerContribution(
    inout TerrainLayerBlend blend,
    StructuredBuffer<GpuTerrainLayerData> layerTable,
    uint storedIndex,
    float contribution,
    float3 terrainNormal,
    GpuTerrainMaterialData terrainData)
{
    if (contribution <= 0.0f)
    {
        return;
    }

    if (storedIndex != 0u)
    {
        uint layerCount = max(terrainData.layerCount, 1u);
        AddTerrainLayerContribution(blend, min(storedIndex - 1u, layerCount - 1u), contribution);
        return;
    }

    AddTerrainAutoLayerContributions(blend, layerTable, contribution, terrainNormal, terrainData);
}

void AccumulateTerrainLayerMapTexel(
    inout TerrainLayerBlend blend,
    Texture2D<float4> indexMap,
    Texture2D<float4> weightMap,
    StructuredBuffer<GpuTerrainLayerData> layerTable,
    uint2 texel,
    float bilinearWeight,
    float3 terrainNormal,
    GpuTerrainMaterialData terrainData)
{
    float4 indexSample = indexMap.Load(int3(texel, 0));
    float4 weightSample = saturate(weightMap.Load(int3(texel, 0))) * bilinearWeight;
    uint4 texelIndices = uint4(round(saturate(indexSample) * 255.0f));
    AddResolvedTerrainLayerContribution(blend, layerTable, texelIndices.x, weightSample.x, terrainNormal, terrainData);
    AddResolvedTerrainLayerContribution(blend, layerTable, texelIndices.y, weightSample.y, terrainNormal, terrainData);
    AddResolvedTerrainLayerContribution(blend, layerTable, texelIndices.z, weightSample.z, terrainNormal, terrainData);
    AddResolvedTerrainLayerContribution(blend, layerTable, texelIndices.w, weightSample.w, terrainNormal, terrainData);
}

TerrainLayerBlend SampleTerrainLayerBlend(
    Texture2D<float4> indexMap,
    Texture2D<float4> weightMap,
    StructuredBuffer<GpuTerrainLayerData> layerTable,
    float2 uv,
    float3 terrainNormal,
    GpuTerrainMaterialData terrainData)
{
    TerrainLayerBlend blend;
    blend.indices = uint4(0, 0, 0, 0);
    blend.weights = float4(0.0f, 0.0f, 0.0f, 0.0f);

    if (terrainData.hasLayerMaps == 0)
    {
        AddTerrainAutoLayerContributions(blend, layerTable, 1.0f, terrainNormal, terrainData);
        return blend;
    }

    uint indexWidth = 1;
    uint indexHeight = 1;
    indexMap.GetDimensions(indexWidth, indexHeight);
    float2 samplePosition = saturate(uv) * float2(indexWidth, indexHeight) - 0.5f;
    float2 basePosition = floor(samplePosition);
    float2 fraction = saturate(samplePosition - basePosition);
    int2 maxTexel = int2(max(indexWidth, 1u) - 1u, max(indexHeight, 1u) - 1u);
    int2 texel00 = clamp(int2(basePosition), int2(0, 0), maxTexel);
    int2 texel10 = clamp(texel00 + int2(1, 0), int2(0, 0), maxTexel);
    int2 texel01 = clamp(texel00 + int2(0, 1), int2(0, 0), maxTexel);
    int2 texel11 = clamp(texel00 + int2(1, 1), int2(0, 0), maxTexel);

    AccumulateTerrainLayerMapTexel(blend, indexMap, weightMap, layerTable, uint2(texel00), (1.0f - fraction.x) * (1.0f - fraction.y), terrainNormal, terrainData);
    AccumulateTerrainLayerMapTexel(blend, indexMap, weightMap, layerTable, uint2(texel10), fraction.x * (1.0f - fraction.y), terrainNormal, terrainData);
    AccumulateTerrainLayerMapTexel(blend, indexMap, weightMap, layerTable, uint2(texel01), (1.0f - fraction.x) * fraction.y, terrainNormal, terrainData);
    AccumulateTerrainLayerMapTexel(blend, indexMap, weightMap, layerTable, uint2(texel11), fraction.x * fraction.y, terrainNormal, terrainData);
    return blend;
}

float4 NormalizeTerrainLayerWeights(float4 weights)
{
    float sum = weights.x + weights.y + weights.z + weights.w;
    if (sum <= 1e-5f)
    {
        return float4(1.0f, 0.0f, 0.0f, 0.0f);
    }

    return weights / sum;
}

float4 MaskInactiveTerrainLayers(float4 weights, uint activeLayerCount)
{
    float4 masked = weights;
    if (activeLayerCount < 4) masked.w = 0.0f;
    if (activeLayerCount < 3) masked.z = 0.0f;
    if (activeLayerCount < 2) masked.y = 0.0f;
    return masked;
}
