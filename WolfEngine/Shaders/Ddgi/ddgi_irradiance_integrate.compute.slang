import "../Common/common_bindless.slang";
import "ddgi_common.slang";

static const uint DDGI_DIRECTION_RESOLUTION = 8u;
static const uint DDGI_DIRECTION_COUNT = DDGI_DIRECTION_RESOLUTION * DDGI_DIRECTION_RESOLUTION;
static const float DDGI_SH_BASIS_L0 = 0.28209479177f;
static const float DDGI_SH_BASIS_L1 = 0.48860251190f;
// A scheduled update contributes one stochastic estimate, regardless of how
// many rendered frames elapsed. Cadence scaling gives sparse, noisy samples
// nearly unit weight (73% at 64 frames) and makes indirect lighting pulse.
static const float DDGI_STABLE_MEAN_BLEND = 0.02f;

struct DdgiVarianceData
{
    float3 mean;
    float3 shortMean;
    float3 variance;
    float3 referenceMean;
};

RWStructuredBuffer<uint4> g_IrradianceEstimator : register(u2);
groupshared uint g_FilteredRadiance[DDGI_DIRECTION_COUNT];
groupshared float3 g_TraceRadiance[DDGI_DIRECTION_COUNT];
groupshared float3 g_TraceDirections[DDGI_DIRECTION_COUNT];
groupshared float3 g_ReferenceRadiance[DDGI_TEMPORAL_REFERENCE_RAY_COUNT];
groupshared float3 g_ReferenceDirections[DDGI_TEMPORAL_REFERENCE_RAY_COUNT];

uint PackRgbe(float3 rgb)
{
    const float maxValue = asfloat(0x477F8000);
    const float minValue = asfloat(0x37800000);
    rgb = clamp(rgb, 0.0f.xxx, maxValue.xxx);
    float maxChannel = max(max(minValue, rgb.r), max(rgb.g, rgb.b));
    float bias = asfloat((asuint(maxChannel) + 0x07804000u) & 0x7F800000u);
    uint3 packedRgb = asuint(rgb + bias.xxx);
    uint exponent = (asuint(bias) << 4u) + 0x10000000u;
    return exponent | (packedRgb.b << 18u) | (packedRgb.g << 9u) | (packedRgb.r & 0x1FFu);
}

float3 UnpackRgbe(uint packed)
{
    float3 rgb = float3(uint3(packed, packed >> 9u, packed >> 18u) & 0x1FFu);
    return ldexp(rgb, int(packed >> 27u) - 24);
}

DdgiVarianceData LoadVarianceData(uint index)
{
    uint4 packed = g_IrradianceEstimator[index];
    DdgiVarianceData data;
    data.mean = UnpackRgbe(packed.x);
    data.shortMean = UnpackRgbe(packed.y);
    data.variance = UnpackRgbe(packed.z);
    data.referenceMean = UnpackRgbe(packed.w);
    return data;
}

void StoreVarianceData(uint index, DdgiVarianceData data)
{
    g_IrradianceEstimator[index] = uint4(
        PackRgbe(data.mean),
        PackRgbe(data.shortMean),
        PackRgbe(data.variance),
        PackRgbe(data.referenceMean));
}

void MultiscaleMeanEstimator(float3 sampleValue, float3 referenceValue, inout DdgiVarianceData data)
{
    // Both reference estimates observe the same directions. Their difference
    // measures actual transport changes (lighting, occluders, emissives, bounces),
    // rather than a different Monte Carlo realization. Keep the reference anchor
    // filtered at the same rate as the output, so response stays fast until the
    // accumulated lighting has caught up, including after a light stops moving.
    float referenceScale = max(max(length(referenceValue), length(data.referenceMean)), 1e-4f);
    float relativeChange = length(referenceValue - data.referenceMean) / referenceScale;
    float meanBlend = clamp(relativeChange * 8.0f, DDGI_STABLE_MEAN_BLEND, 1.0f);

    float shortMeanBlend = max(clamp(irradianceTemporalBlendSpeed, 1.0f / 256.0f, 1.0f), meanBlend);
    float3 deviation = sqrt(max(1e-5f.xxx, data.variance));
    float3 highThreshold = 0.1f.xxx + data.shortMean + deviation * 8.0f;
    // Preserve firefly rejection when the reference is unchanged. Actual
    // increases must not be clipped against the previous, potentially dark sky.
    if (meanBlend <= DDGI_STABLE_MEAN_BLEND)
    {
        sampleValue = min(sampleValue, highThreshold);
    }

    float3 delta = sampleValue - data.shortMean;
    data.shortMean = lerp(data.shortMean, sampleValue, shortMeanBlend);
    float3 delta2 = sampleValue - data.shortMean;
    data.variance = lerp(data.variance, max(0.0f.xxx, delta * delta2), shortMeanBlend * 0.5f);
    data.mean = lerp(data.mean, sampleValue, meanBlend);
    data.referenceMean = lerp(data.referenceMean, referenceValue, meanBlend);
}

float3 IntegrateDirection(float3 direction, uint rayCount)
{
    float3 radiance = 0.0f.xxx;
    float weightSum = 0.0f;
    for (uint rayIndex = 0u; rayIndex < rayCount; rayIndex++)
    {
        float weight = saturate(dot(direction, g_TraceDirections[rayIndex]));
        radiance += g_TraceRadiance[rayIndex] * weight;
        weightSum += weight;
    }
    return weightSum > 1e-5f ? radiance / weightSum : 0.0f.xxx;
}

float3 IntegrateReferenceDirection(float3 direction)
{
    float3 radiance = 0.0f.xxx;
    float weightSum = 0.0f;
    for (uint rayIndex = 0u; rayIndex < DDGI_TEMPORAL_REFERENCE_RAY_COUNT; rayIndex++)
    {
        float weight = saturate(dot(direction, g_ReferenceDirections[rayIndex]));
        radiance += g_ReferenceRadiance[rayIndex] * weight;
        weightSum += weight;
    }
    return weightSum > 1e-5f ? radiance / weightSum : 0.0f.xxx;
}

void CopyHistory(uint probeIndex)
{
    uint2 pixel = DdgiProbeStatePixel(probeIndex);
    Texture2D<float4> historyL0 = GetTexture(irradianceL0HistoryReadHandle);
    Texture2D<float4> historyLy = GetTexture(irradianceLyHistoryReadHandle);
    Texture2D<float4> historyLz = GetTexture(irradianceLzHistoryReadHandle);
    Texture2D<float4> historyLx = GetTexture(irradianceLxHistoryReadHandle);
    RWTexture2D<float4> outputL0 = GetRWTexture(irradianceL0HistoryWriteHandle);
    RWTexture2D<float4> outputLy = GetRWTexture(irradianceLyHistoryWriteHandle);
    RWTexture2D<float4> outputLz = GetRWTexture(irradianceLzHistoryWriteHandle);
    RWTexture2D<float4> outputLx = GetRWTexture(irradianceLxHistoryWriteHandle);
    outputL0[pixel] = historyL0.Load(int3(pixel, 0));
    outputLy[pixel] = historyLy.Load(int3(pixel, 0));
    outputLz[pixel] = historyLz.Load(int3(pixel, 0));
    outputLx[pixel] = historyLx.Load(int3(pixel, 0));
}

[numthreads(8, 8, 1)]
void DdgiIrradianceIntegrateCS(
    uint3 groupId : SV_GroupID,
    uint3 groupThreadId : SV_GroupThreadID,
    uint groupIndex : SV_GroupIndex)
{
    uint probeIndex = groupId.x;
    if (probeIndex >= probeCount)
    {
        return;
    }

    if (!DdgiIsProbeUpdateActive(probeIndex))
    {
        if (groupIndex == 0u)
        {
            CopyHistory(probeIndex);
        }
        return;
    }

    uint rayCount = DdgiRaySampleCount(irradianceTileInteriorSize);
    if (groupIndex < rayCount)
    {
        uint2 samplePixel = DdgiRaySamplePixel(
            probeIndex,
            groupIndex,
            irradianceTileInteriorSize);
        g_TraceRadiance[groupIndex] =
            GetTexture(traceIrradianceReadHandle).Load(int3(samplePixel, 0)).rgb;
        g_TraceDirections[groupIndex] =
            DdgiRayDirection(groupIndex, rayCount, probeIndex);
    }
    if (groupIndex < DDGI_TEMPORAL_REFERENCE_RAY_COUNT)
    {
        g_ReferenceRadiance[groupIndex] = GetTexture(traceIrradianceReadHandle)
            .Load(int3(DdgiReferenceRayPixel(probeIndex, groupIndex), 0)).rgb;
        g_ReferenceDirections[groupIndex] = DdgiReferenceRayDirection(groupIndex, probeIndex);
    }
    GroupMemoryBarrierWithGroupSync();

    float2 octUv = ((float2(groupThreadId.xy) + 0.5f) / float(DDGI_DIRECTION_RESOLUTION)) * 2.0f - 1.0f;
    float3 direction = DdgiOctDecode(octUv);
    float3 currentRadiance = IntegrateDirection(direction, rayCount);
    float3 referenceRadiance = IntegrateReferenceDirection(direction);
    uint estimatorIndex = DdgiPhysicalProbeIndex(probeIndex) * DDGI_DIRECTION_COUNT + groupIndex;
    DdgiVarianceData varianceData;
    if (!DdgiProbeHasUsableIntegrationHistory(probeIndex))
    {
        varianceData = (DdgiVarianceData)0;
        varianceData.mean = currentRadiance;
        varianceData.shortMean = currentRadiance;
        varianceData.referenceMean = referenceRadiance;
    }
    else
    {
        varianceData = LoadVarianceData(estimatorIndex);
    }
    MultiscaleMeanEstimator(currentRadiance, referenceRadiance, varianceData);
    StoreVarianceData(estimatorIndex, varianceData);
    g_FilteredRadiance[groupIndex] = PackRgbe(varianceData.mean);

    GroupMemoryBarrierWithGroupSync();
    if (groupIndex != 0u)
    {
        return;
    }

    float3 coefficientL0 = 0.0f.xxx;
    float3 coefficientLy = 0.0f.xxx;
    float3 coefficientLz = 0.0f.xxx;
    float3 coefficientLx = 0.0f.xxx;
    float solidAngleWeightSum = 0.0f;
    for (uint sampleIndex = 0u; sampleIndex < DDGI_DIRECTION_COUNT; sampleIndex++)
    {
        uint2 sampleCoord = uint2(sampleIndex % DDGI_DIRECTION_RESOLUTION, sampleIndex / DDGI_DIRECTION_RESOLUTION);
        float2 sampleUv = ((float2(sampleCoord) + 0.5f) / float(DDGI_DIRECTION_RESOLUTION)) * 2.0f - 1.0f;
        float3 sampleDirection = DdgiOctDecode(sampleUv);
        float3 sampleRadiance = UnpackRgbe(g_FilteredRadiance[sampleIndex]);
        float3 unnormalizedDirection = float3(
            sampleUv.x,
            sampleUv.y,
            1.0f - abs(sampleUv.x) - abs(sampleUv.y));
        float solidAngleWeight = rcp(max(
            dot(unnormalizedDirection, unnormalizedDirection) *
            length(unnormalizedDirection),
            1e-5f));
        coefficientL0 += sampleRadiance * (DDGI_SH_BASIS_L0 * solidAngleWeight);
        coefficientLy += sampleRadiance * (DDGI_SH_BASIS_L1 * sampleDirection.y * solidAngleWeight);
        coefficientLz += sampleRadiance * (DDGI_SH_BASIS_L1 * sampleDirection.z * solidAngleWeight);
        coefficientLx += sampleRadiance * (DDGI_SH_BASIS_L1 * sampleDirection.x * solidAngleWeight);
        solidAngleWeightSum += solidAngleWeight;
    }

    float projectionScale = 4.0f * WOLF_PI / max(solidAngleWeightSum, 1e-5f);
    uint2 outputPixel = DdgiProbeStatePixel(probeIndex);
    RWTexture2D<float4> outputL0 = GetRWTexture(irradianceL0HistoryWriteHandle);
    RWTexture2D<float4> outputLy = GetRWTexture(irradianceLyHistoryWriteHandle);
    RWTexture2D<float4> outputLz = GetRWTexture(irradianceLzHistoryWriteHandle);
    RWTexture2D<float4> outputLx = GetRWTexture(irradianceLxHistoryWriteHandle);
    outputL0[outputPixel] = float4(coefficientL0 * projectionScale, 0.0f);
    outputLy[outputPixel] = float4(coefficientLy * projectionScale, 0.0f);
    outputLz[outputPixel] = float4(coefficientLz * projectionScale, 0.0f);
    outputLx[outputPixel] = float4(coefficientLx * projectionScale, 0.0f);
}
