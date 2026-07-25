import "common.slang";
import "common_bindless.slang";
import "common_lighting.slang";
import "reflections_common.slang";

cbuffer BindlessHandles : register(b0)
{
    uint depthHandle;
    uint normalHandle;
    uint materialHandle;
    uint velocityHandle;
    uint outputHandle;
    uint samplerHandle;
};

cbuffer CameraParams : register(b1)
{
    CameraParams camera;
    row_major float4x4 viewProjection;
};

cbuffer ReflectionSettings : register(b2)
{
    uint framebufferSizeX;
    uint framebufferSizeY;
    uint maxSteps;
    uint binarySearchSteps;
    float maxRayDistance;
    float thickness;
    float bias;
    float maxRoughness;
    float edgeFade;
    float intensity;
    float reprojectionStrength;
    uint colorPyramidLevelCount;
    uint colorPyramidValid;
    uint colorPyramidHandles[REFLECTION_MAX_COLOR_PYRAMID_LEVELS];
};

struct ScreenTraceResult
{
    float2 uv;
    float confidence;
    float rayDistance;
    bool hit;
};

bool ProjectCameraRelativePositionUnbounded(float3 position, out float2 uv)
{
    float viewDepth = mul(float4(position, 1.0f), camera.viewMatrix).z;
    if (viewDepth <= camera.nearPlane)
    {
        uv = 0.0f.xx;
        return false;
    }

    float4 clip = mul(float4(position, 1.0f), viewProjection);
    if (clip.w <= 1e-5f)
    {
        uv = 0.0f.xx;
        return false;
    }

    float2 ndc = clip.xy / clip.w;
    uv = float2(ndc.x * 0.5f + 0.5f, -ndc.y * 0.5f + 0.5f);
    return true;
}

bool ProjectCameraRelativePosition(float3 position, out float2 uv)
{
    if (!ProjectCameraRelativePositionUnbounded(position, uv))
    {
        return false;
    }

    return all(uv > 0.0f.xx) && all(uv < 1.0f.xx);
}

// Reflection direction and origin for an arbitrary pixel, used to measure how quickly the
// reflected image moves across neighbouring pixels.
bool ReflectionRayAt(uint2 pixel, uint2 size, out float3 origin, out float3 direction)
{
    origin = 0.0f.xxx;
    direction = 0.0f.xxx;
    float depth = GetTexture(depthHandle).Load(int3(pixel, 0)).r;
    if (IsBackgroundDepth(depth))
    {
        return false;
    }

    float3 normal = DecodeNormal(GetTexture(normalHandle).Load(int3(pixel, 0)).xyz);
    float2 ndc = float2(
        (float(pixel.x) + 0.5f) / float(size.x) * 2.0f - 1.0f,
        -((float(pixel.y) + 0.5f) / float(size.y) * 2.0f - 1.0f));
    float4 positionH = mul(float4(ndc, depth, 1.0f), camera.invViewProjection);
    float3 position = positionH.xyz / positionH.w;
    float3 viewDirection = normalize(-position);
    origin = position + normal * bias;
    direction = normalize(reflect(-viewDirection, normal));
    return true;
}

/// Where a neighbouring pixel's reflection ray would land at the same ray distance. Reusing the
/// centre pixel's distance avoids a second march; it varies slowly compared to the direction,
/// which is what curvature makes swing. Falls back to the centre uv when the neighbour is not a
/// comparable surface, so silhouettes stay sharp instead of over-blurring.
float2 NeighbourReflectionHitUv(uint2 pixel, uint2 size, float rayDistance, float2 fallbackUv)
{
    float3 origin;
    float3 direction;
    if (!ReflectionRayAt(pixel, size, origin, direction))
    {
        return fallbackUv;
    }

    float2 uv;
    if (!ProjectCameraRelativePositionUnbounded(origin + direction * rayDistance, uv))
    {
        return fallbackUv;
    }

    return uv;
}

ScreenTraceResult TraceScreenSpaceReflection(float3 origin, float3 direction)
{
    ScreenTraceResult result;
    result.uv = 0.0f.xx;
    result.confidence = 0.0f;
    result.rayDistance = 0.0f;
    result.hit = false;

    Texture2D<float4> depthTexture = GetTexture(depthHandle);
    Texture2D<float4> normalTexture = GetTexture(normalHandle);
    uint2 size = uint2(framebufferSizeX, framebufferSizeY);
    float previousDistance = max(bias, 0.001f);
    float previousDepthDelta = 0.0f;
    bool previousSampleValid = false;
    for (uint stepIndex = 1u; stepIndex <= maxSteps; stepIndex++)
    {
        float normalizedStep = float(stepIndex) / float(max(maxSteps, 1u));
        // Quadratic spacing spends samples near the reflector, where thin geometry
        // and contact reflections are most sensitive to undersampling.
        float rayDistance = maxRayDistance * normalizedStep * normalizedStep;
        float3 candidate = origin + direction * rayDistance;
        float2 uv;
        if (!ProjectCameraRelativePosition(candidate, uv))
        {
            break;
        }

        uint2 samplePixel = min(uint2(uv * float2(size)), size - 1u);
        float sceneDepth = depthTexture.Load(int3(samplePixel, 0)).r;
        if (IsBackgroundDepth(sceneDepth))
        {
            previousDistance = rayDistance;
            previousSampleValid = false;
            continue;
        }

        float candidateViewDepth = mul(float4(candidate, 1.0f), camera.viewMatrix).z;
        float sceneViewDepth = ReconstructViewPosition(samplePixel, size, sceneDepth, camera.invProjection).z;
        float depthDelta = candidateViewDepth - sceneViewDepth;

        // A valid screen-space hit is a crossing from in front of the depth
        // buffer to behind it. Comparing radial camera distance here produces
        // false crossings on curved reflectors and is the source of horizontal
        // banding. Requiring an actual bracket also rejects the first sample,
        // which is commonly the reflector itself.
        if (previousSampleValid && previousDepthDelta < 0.0f && depthDelta >= 0.0f)
        {
            float low = previousDistance;
            float high = rayDistance;
            float2 refinedUv = uv;
            [loop]
            for (uint binaryStep = 0u; binaryStep < binarySearchSteps; binaryStep++)
            {
                float middle = (low + high) * 0.5f;
                float3 middlePosition = origin + direction * middle;
                float2 middleUv;
                if (!ProjectCameraRelativePosition(middlePosition, middleUv))
                {
                    high = middle;
                    continue;
                }
                uint2 middlePixel = min(uint2(middleUv * float2(size)), size - 1u);
                float middleDepth = depthTexture.Load(int3(middlePixel, 0)).r;
                if (IsBackgroundDepth(middleDepth))
                {
                    low = middle;
                    continue;
                }
                float middleViewDepth = mul(float4(middlePosition, 1.0f), camera.viewMatrix).z;
                float middleSceneViewDepth =
                    ReconstructViewPosition(middlePixel, size, middleDepth, camera.invProjection).z;
                if (middleViewDepth >= middleSceneViewDepth)
                {
                    high = middle;
                    refinedUv = middleUv;
                }
                else
                {
                    low = middle;
                }
            }

            float3 refinedPosition = origin + direction * high;
            if (!ProjectCameraRelativePosition(refinedPosition, refinedUv))
            {
                return result;
            }
            uint2 refinedPixel = min(uint2(refinedUv * float2(size)), size - 1u);
            float refinedSceneDepth = depthTexture.Load(int3(refinedPixel, 0)).r;
            if (IsBackgroundDepth(refinedSceneDepth))
            {
                return result;
            }
            float refinedRayViewDepth =
                mul(float4(refinedPosition, 1.0f), camera.viewMatrix).z;
            float refinedSceneViewDepth =
                ReconstructViewPosition(refinedPixel, size, refinedSceneDepth, camera.invProjection).z;
            float refinedDepthDelta = refinedRayViewDepth - refinedSceneViewDepth;
            float adaptiveThickness =
                thickness * (1.0f + refinedSceneViewDepth * 0.01f);
            if (refinedDepthDelta < 0.0f || refinedDepthDelta > adaptiveThickness)
            {
                previousDistance = rayDistance;
                previousDepthDelta = depthDelta;
                previousSampleValid = true;
                continue;
            }

            float3 hitNormal =
                DecodeNormal(normalTexture.Load(int3(refinedPixel, 0)).xyz);
            float frontFaceConfidence =
                saturate(dot(hitNormal, -direction) * 4.0f);
            if (frontFaceConfidence <= 0.0f)
            {
                previousDistance = rayDistance;
                previousDepthDelta = depthDelta;
                previousSampleValid = true;
                continue;
            }

            result.uv = refinedUv;
            result.rayDistance = high;
            result.confidence =
                ReflectionScreenEdgeWeight(refinedUv, edgeFade) *
                saturate(1.0f - high / max(maxRayDistance, 1e-4f)) *
                saturate(1.0f - refinedDepthDelta / max(adaptiveThickness, 1e-4f)) *
                frontFaceConfidence;
            result.hit = result.confidence > 0.0f;
            return result;
        }
        previousDistance = rayDistance;
        previousDepthDelta = depthDelta;
        previousSampleValid = true;
    }
    return result;
}

[numthreads(8, 8, 1)]
void ReflectionsScreenSpaceCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadId.xy;
    uint2 size = uint2(framebufferSizeX, framebufferSizeY);
    if (any(pixel >= size))
    {
        return;
    }

    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    outputTexture[pixel] = 0.0f.xxxx;
    float depth = GetTexture(depthHandle).Load(int3(pixel, 0)).r;
    if (IsBackgroundDepth(depth) || colorPyramidValid == 0u)
    {
        return;
    }

    float roughness = GetTexture(materialHandle).Load(int3(pixel, 0)).g;
    float roughnessFade = ReflectionRoughnessFade(roughness, maxRoughness);
    if (roughnessFade <= 0.0f || intensity <= 0.0f)
    {
        return;
    }

    float3 normal = DecodeNormal(GetTexture(normalHandle).Load(int3(pixel, 0)).xyz);
    float2 ndc = float2(
        (float(pixel.x) + 0.5f) / float(size.x) * 2.0f - 1.0f,
        -((float(pixel.y) + 0.5f) / float(size.y) * 2.0f - 1.0f));
    float4 positionH = mul(float4(ndc, depth, 1.0f), camera.invViewProjection);
    float3 position = positionH.xyz / positionH.w;
    float3 viewDirection = normalize(-position);
    float3 reflectionDirection = normalize(reflect(-viewDirection, normal));
    ScreenTraceResult trace = TraceScreenSpaceReflection(position + normal * bias, reflectionDirection);
    if (!trace.hit)
    {
        return;
    }

    // Curvature, not roughness, is what minifies this sphere's reflection of the street, so
    // measure the reflected image's spread across neighbouring pixels and let that drive the
    // pyramid level.
    float footprintLevel = ReflectionFootprintLevel(
        trace.uv,
        NeighbourReflectionHitUv(uint2(min(pixel.x + 1u, size.x - 1u), pixel.y), size, trace.rayDistance, trace.uv),
        NeighbourReflectionHitUv(uint2(pixel.x, min(pixel.y + 1u, size.y - 1u)), size, trace.rayDistance, trace.uv),
        size);

    // The trace runs against this frame's depth buffer but the only shaded color available
    // ahead of deferred lighting is last frame's. Reproject the hit through the hit pixel's
    // motion vector so the pyramid fetch lands on the same surface point.
    float historyConfidence;
    float3 reflectedRadiance = SampleReflectionColorPyramid(
        colorPyramidHandles,
        colorPyramidLevelCount,
        samplerHandle,
        velocityHandle,
        size,
        trace.uv,
        roughness,
        edgeFade,
        footprintLevel,
        reprojectionStrength,
        historyConfidence);
    float weight = saturate(trace.confidence * roughnessFade * historyConfidence);
    outputTexture[pixel] = float4(reflectedRadiance * intensity, weight);
}
