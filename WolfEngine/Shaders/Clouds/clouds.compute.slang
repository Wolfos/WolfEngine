import "../Common/common.slang";
import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";
import "../Common/cloud_sampling.slang";
import "../Atmosphere/atmosphere_common.slang";
import "cloud_common.slang";

/// The volumetric cloud layer: the per-view march at reduced resolution and its temporal accumulation, the sun
/// shadow map, and the clouds drawn into the sky environment for image-based lighting.

cbuffer BindlessHandles : register(b0)
{
    uint weatherHandle;
    uint shapeHandle;
    uint detailHandle;
    uint wrapSamplerHandle;
    uint linearSamplerHandle;
    uint transmittanceLutHandle;
    uint skyViewLutHandle;
    uint aerialPerspectiveHandle;
    uint irradianceHandle;
    uint sceneDepthHandle;
    uint currentColorHandle;
    uint currentDepthHandle;
    uint historyColorHandle;
    uint historyDepthHandle;
    uint outputColorHandle;
    uint outputDepthHandle;
};

cbuffer AtmosphereParams : register(b1)
{
    AtmosphereConstants atmosphere;
};

cbuffer CloudParams : register(b2)
{
    CloudConstants clouds;
};

cbuffer CloudViewParams : register(b11)
{
    /// Camera-relative and unjittered: clouds trace fixed directions, so the history sees a stable signal.
    row_major float4x4 invViewProjection;
    /// Camera-relative and jittered like the G-buffer, to reconstruct scene depth.
    row_major float4x4 depthInvViewProjection;
    /// Relative to the previous camera origin, unjittered.
    row_major float4x4 previousViewProjection;
    /// xyz: this frame's camera origin minus the previous one (m). w: history weight for a still camera.
    float4 cameraDelta;
    /// xy: how far the wind moved the clouds since the previous frame (m, world XZ). z: jitter offset (0..1).
    float4 windDelta;
    /// xy: shadow map centre (camera-relative XZ, m). z: shadow map extent (m). w: unused.
    float4 shadowMap;
    uint traceSizeX;
    uint traceSizeY;
    uint renderSizeX;
    uint renderSizeY;
    uint traceDivisor;
    uint historyValid;
    /// The output of a shadow or environment dispatch.
    uint outputSizeX;
    uint outputSizeY;
};

uint2 TraceSize()
{
    return uint2(traceSizeX, traceSizeY);
}

uint2 RenderSize()
{
    return uint2(renderSizeX, renderSizeY);
}

uint2 OutputSize()
{
    return uint2(outputSizeX, outputSizeY);
}

CloudTextures MakeCloudTextures()
{
    CloudTextures textures;
    textures.weather = weatherHandle;
    textures.shape = shapeHandle;
    textures.detail = detailHandle;
    textures.wrapSampler = wrapSamplerHandle;

    return textures;
}

AtmosphereLuts MakeCloudAtmosphereLuts()
{
    return MakeAtmosphereLuts(transmittanceLutHandle, 0u, linearSamplerHandle);
}

/// Per-pixel blue-ish noise that changes every frame by the golden ratio, which spreads consecutive frames' samples
/// evenly for the temporal accumulation to average.
float CloudJitter(uint2 pixel)
{
    float noise = frac(52.9829189f * frac(dot(float2(pixel) + 0.5f, float2(0.06711056f, 0.00583715f))));

    return frac(noise + windDelta.z);
}

float3 TraceDirection(uint2 tracePixel)
{
    return ReconstructWorldDirection(tracePixel, TraceSize(), 1.0f, invViewProjection);
}

/// The full-resolution pixel a trace pixel stands for. Fixed rather than jittered, so the scene depth it was
/// traced against stays put from frame to frame.
uint2 RepresentativePixel(uint2 tracePixel)
{
    uint divisor = max(traceDivisor, 1u);

    return min(tracePixel * divisor + divisor / 2u, RenderSize() - 1u);
}

/// Distance from the camera to the opaque scene behind a render pixel (m), or the background distance.
float SceneDistance(uint2 renderPixel)
{
    float depth = GetTexture(sceneDepthHandle).Load(int3(renderPixel, 0)).r;
    if (IsBackgroundDepth(depth))
    {
        return CLOUD_BACKGROUND_DISTANCE_KM * 1000.0f;
    }

    float2 ndc;
    ndc.x = (float(renderPixel.x) + 0.5f) / float(renderSizeX) * 2.0f - 1.0f;
    ndc.y = -((float(renderPixel.y) + 0.5f) / float(renderSizeY) * 2.0f - 1.0f);
    float4 position = mul(float4(ndc, depth, 1.0f), depthInvViewProjection);

    return length(position.xyz / position.w);
}

/// Folds the atmosphere between the camera and the cloud into the cloud's radiance, so compositing over a
/// colour that already carries aerial perspective (the sky, or a lit surface) never applies it twice:
/// sky = inscatter(0..d) + T(0..d) * sky(d..), and the cloud replaces the part behind it.
float3 ApplyCloudAerialPerspective(float3 radiance, float transmittance, float3 inscatter, float3 atmosphereTransmittance)
{
    return atmosphereTransmittance * radiance + inscatter * (1.0f - transmittance);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void CloudMarchCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= TraceSize()))
    {
        return;
    }

    float3 direction = TraceDirection(id.xy);
    float sceneDistance = SceneDistance(RepresentativePixel(id.xy));
    float maxDistance = min(sceneDistance, clouds.sampling.z);
    float2 range = CloudLayerIntersect(clouds, direction, maxDistance);

    float3 ambientTop;
    float3 ambientBottom;
    SampleCloudAmbient(irradianceHandle, linearSamplerHandle, ambientTop, ambientBottom);

    CloudMarchResult result = MarchClouds(
        clouds,
        atmosphere,
        MakeCloudAtmosphereLuts(),
        MakeCloudTextures(),
        direction,
        range,
        CloudJitter(id.xy),
        uint(clouds.sampling.x),
        uint(clouds.sampling.y),
        ambientTop,
        ambientBottom);

    float2 uv = (float2(id.xy) + 0.5f) / float2(TraceSize());
    float4 aerialPerspective = SampleAerialPerspective(aerialPerspectiveHandle, linearSamplerHandle, uv, result.depth);
    float3 radiance = ApplyCloudAerialPerspective(
        result.radiance,
        result.transmittance,
        aerialPerspective.rgb,
        (1.0f - aerialPerspective.a).xxx);

    GetRWTexture(outputColorHandle)[id.xy] = float4(radiance, result.transmittance);
    GetRWTexture(outputDepthHandle)[id.xy] = float4(result.depth * 0.001f, sceneDistance * 0.001f, 0.0f, 0.0f);
}

/// Clips a history sample toward the current neighbourhood's mean, inside a box of gamma standard deviations
/// (Salvi 2016). Generous enough to keep accumulating jittered samples, tight enough to drop stale clouds.
float4 ClipToNeighbourhood(float4 history, float4 mean, float4 deviation)
{
    float4 extent = max(deviation, 1e-4f);
    float4 offset = history - mean;
    float4 units = abs(offset / extent);
    float largest = max(max(units.x, units.y), max(units.z, units.w));
    if (largest > 1.0f)
    {
        return mean + offset / largest;
    }

    return history;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void CloudTemporalCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= TraceSize()))
    {
        return;
    }

    Texture2D<float4> currentColor = GetTexture(currentColorHandle);
    Texture2D<float4> currentDepth = GetTexture(currentDepthHandle);
    float4 color = currentColor.Load(int3(id.xy, 0));
    float4 depth = currentDepth.Load(int3(id.xy, 0));

    RWTexture2D<float4> outputColor = GetRWTexture(outputColorHandle);
    RWTexture2D<float4> outputDepth = GetRWTexture(outputDepthHandle);
    if (historyValid == 0u)
    {
        outputColor[id.xy] = color;
        outputDepth[id.xy] = depth;
        return;
    }

    // Where the cloud mass seen through this pixel was last frame: carried by the camera's motion and the wind.
    float3 position = TraceDirection(id.xy) * (depth.x * 1000.0f);
    float3 previousPosition = position + cameraDelta.xyz - float3(windDelta.x, 0.0f, windDelta.y);
    float4 previousClip = mul(float4(previousPosition, 1.0f), previousViewProjection);
    float2 uv = (float2(id.xy) + 0.5f) / float2(TraceSize());
    float2 previousUv = uv;
    bool reprojected = previousClip.w > 1e-5f;
    if (reprojected)
    {
        float2 previousNdc = previousClip.xy / previousClip.w;
        previousUv = float2(previousNdc.x * 0.5f + 0.5f, 0.5f - previousNdc.y * 0.5f);
        reprojected = all(previousUv >= 0.0f) && all(previousUv <= 1.0f);
    }

    if (reprojected == false)
    {
        outputColor[id.xy] = color;
        outputDepth[id.xy] = depth;
        return;
    }

    float4 moment1 = 0.0f;
    float4 moment2 = 0.0f;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 neighbour = clamp(int2(id.xy) + int2(x, y), int2(0, 0), int2(TraceSize()) - 1);
            float4 value = currentColor.Load(int3(neighbour, 0));
            moment1 += value;
            moment2 += value * value;
        }
    }

    float4 mean = moment1 / 9.0f;
    float4 deviation = sqrt(max(moment2 / 9.0f - mean * mean, 0.0f)) * 1.5f;

    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    float4 history = GetTexture(historyColorHandle).SampleLevel(linearSampler, previousUv, 0.0f);
    float4 historyDepth = GetTexture(historyDepthHandle).SampleLevel(linearSampler, previousUv, 0.0f);
    history = ClipToNeighbourhood(history, mean, deviation);

    // The opaque scene this pixel was traced against moved, for example a silhouette crossed it: the history
    // describes a ray of a different length.
    float sceneDifference = abs(historyDepth.y - depth.y) / max(max(historyDepth.y, depth.y), 1e-3f);
    float weight = sceneDifference > 0.1f
        ? 0.0f
        : cameraDelta.w;

    // Fast motion leaves less of the history trustworthy; drop it faster so pans do not trail.
    float motionPixels = length((previousUv - uv) * float2(TraceSize()));
    weight *= lerp(1.0f, 0.7f, saturate(motionPixels / 8.0f));

    outputColor[id.xy] = lerp(color, history, weight);
    outputDepth[id.xy] = float4(lerp(depth.x, historyDepth.x, weight), depth.y, 0.0f, 0.0f);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void CloudShadowCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= OutputSize()))
    {
        return;
    }

    RWTexture2D<float4> output = GetRWTexture(outputColorHandle);
    float3 sunDirection = clouds.sunDirection.xyz;
    if (sunDirection.y <= 0.01f)
    {
        output[id.xy] = 1.0f;
        return;
    }

    // Each texel is a vertical column of the layer, crossed along the sun's direction from base to top.
    float2 offset = ((float2(id.xy) + 0.5f) / float2(OutputSize()) - 0.5f) * shadowMap.z;
    float2 centre = shadowMap.xy + offset;
    float baseHeight = CloudBaseAltitude(clouds) - CloudCameraAltitude(clouds);
    float topHeight = CloudTopAltitude(clouds) - CloudCameraAltitude(clouds);
    float3 entry = float3(centre.x, baseHeight, centre.y);
    float pathLength = min((topHeight - baseHeight) / sunDirection.y, 12000.0f);

    const uint sampleCount = 16u;
    float stepLength = pathLength / float(sampleCount);
    CloudTextures textures = MakeCloudTextures();
    float opticalDepth = 0.0f;
    for (uint i = 0u; i < sampleCount; i++)
    {
        float3 position = entry + sunDirection * (stepLength * (float(i) + 0.5f));
        CloudSamplePoint samplePoint = MakeCloudSamplePoint(clouds, position);
        opticalDepth += SampleCloudDensity(clouds, textures, samplePoint, true) * stepLength;
    }

    output[id.xy] = float4(exp(-opticalDepth), 0.0f, 0.0f, 1.0f);
}

/// Transmittance of the atmosphere along a direction from the camera to a point, from the transmittance LUT. A
/// ray toward the ground cannot be looked up from the camera, so it is looked up in reverse.
float3 AtmosphereTransmittanceBetween(AtmosphereParameters parameters, AtmosphereLuts luts, float3 direction, float3 position)
{
    float cameraRadius = atmosphere.view.x;
    float pointRadius = (CloudGroundRadius(clouds) + CloudAltitude(clouds, position)) * 0.001f;
    float3 pointUp = normalize(float3(position.x, clouds.layer.w + position.y, position.z));
    float cameraMu = direction.y;
    float pointMu = dot(pointUp, direction);
    if (direction.y >= 0.0f)
    {
        float3 fromCamera = SampleTransmittanceLut(parameters, luts, cameraRadius, cameraMu);
        float3 fromPoint = SampleTransmittanceLut(parameters, luts, pointRadius, pointMu);

        return saturate(fromCamera / max(fromPoint, 1e-6f));
    }

    float3 fromPointBack = SampleTransmittanceLut(parameters, luts, pointRadius, -pointMu);
    float3 fromCameraBack = SampleTransmittanceLut(parameters, luts, cameraRadius, -cameraMu);

    return saturate(fromPointBack / max(fromCameraBack, 1e-6f));
}

/// The clouds seen from the camera in every direction, as an equirectangular image composited over the sky
/// environment afterwards. rgb is radiance with the atmosphere in front of the clouds folded in, a transmittance.
[shader("compute")]
[numthreads(8, 8, 1)]
void CloudEnvironmentMarchCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= OutputSize()))
    {
        return;
    }

    float2 uv = (float2(id.xy) + 0.5f) / float2(OutputSize());
    float3 direction = EnvUVToDir(uv);
    float2 range = CloudLayerIntersect(clouds, direction, clouds.sampling.z);

    float3 ambientTop;
    float3 ambientBottom;
    SampleCloudAmbient(irradianceHandle, linearSamplerHandle, ambientTop, ambientBottom);

    AtmosphereLuts luts = MakeCloudAtmosphereLuts();
    CloudMarchResult result = MarchClouds(
        clouds,
        atmosphere,
        luts,
        MakeCloudTextures(),
        direction,
        range,
        CloudJitter(id.xy),
        uint(clouds.sampling.x),
        uint(clouds.sampling.y),
        ambientTop,
        ambientBottom);

    float3 radiance = 0.0f;
    if (result.transmittance < 1.0f)
    {
        AtmosphereParameters parameters = GetAtmosphereParameters(atmosphere);
        float3 atmosphereTransmittance = AtmosphereTransmittanceBetween(parameters, luts, direction, direction * result.depth);
        float3 sky = SampleSkyViewLut(atmosphere, skyViewLutHandle, linearSamplerHandle, direction);
        float3 inscatter = sky * (1.0f - atmosphereTransmittance);
        radiance = ApplyCloudAerialPerspective(result.radiance, result.transmittance, inscatter, atmosphereTransmittance);
    }

    GetRWTexture(outputColorHandle)[id.xy] = float4(radiance, result.transmittance);
}

/// Composites the cloud environment over the sky environment in place.
[shader("compute")]
[numthreads(8, 8, 1)]
void CloudEnvironmentCompositeCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= OutputSize()))
    {
        return;
    }

    float2 uv = (float2(id.xy) + 0.5f) / float2(OutputSize());
    float4 cloud = GetTexture(currentColorHandle).SampleLevel(GetSampler(linearSamplerHandle), uv, 0.0f);
    RWTexture2D<float4> environment = GetRWTexture(outputColorHandle);
    float4 sky = environment[id.xy];

    environment[id.xy] = float4(sky.rgb * cloud.a + cloud.rgb, sky.a);
}
