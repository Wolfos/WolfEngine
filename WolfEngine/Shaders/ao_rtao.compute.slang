import "common.slang";
import "common_bindless.slang";
import "common_lighting.slang";

cbuffer BindlessHandles : register(b0)
{
    uint depthHandle;
    uint normalHandle;
    uint outputHandle;
};

cbuffer CameraParams : register(b1)
{
    CameraParams camera;
};

cbuffer AoSettings : register(b2)
{
    uint fullResolutionX;
    uint fullResolutionY;
    uint outputResolutionX;
    uint outputResolutionY;
    float radius;
    float bias;
    float strength;
    uint frameIndex;
};

RaytracingAccelerationStructure sceneTlas : register(t3);

float Hash12(uint2 pixel, uint frame)
{
    uint state = pixel.x * 747796405u + pixel.y * 2891336453u + frame * 277803737u + 0x9E3779B9u;
    state = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    state = (state >> 22u) ^ state;
    return float(state) / 4294967295.0f;
}

float3 BuildHemisphereDirection(float3 normal, float randomAngle)
{
    float3 helper = abs(normal.y) < 0.999f ? float3(0.0f, 1.0f, 0.0f) : float3(1.0f, 0.0f, 0.0f);
    float3 tangent = normalize(cross(helper, normal));
    float3 bitangent = cross(normal, tangent);
    float angle = randomAngle * 6.28318530718f;
    float r = 0.70710678f;
    return normalize(normal * r + tangent * cos(angle) * r + bitangent * sin(angle) * r);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadId.xy;
    uint2 outputSize = uint2(outputResolutionX, outputResolutionY);
    if (pixel.x >= outputSize.x || pixel.y >= outputSize.y)
    {
        return;
    }

    Texture2D<float4> depthTexture = GetTexture(depthHandle);
    Texture2D<float4> normalTexture = GetTexture(normalHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);

    uint2 fullSize = uint2(fullResolutionX, fullResolutionY);
    uint2 sourcePixel = uint2((float2(pixel) + 0.5f) / float2(outputSize) * float2(fullSize));
    sourcePixel = min(sourcePixel, fullSize - 1u);

    float depth = depthTexture.Load(int3(sourcePixel, 0)).r;
    if (IsBackgroundDepth(depth))
    {
        outputTexture[pixel] = float4(1.0f, 1.0f, 1.0f, 1.0f);
        return;
    }

    float3 normal = DecodeNormal(normalTexture.Load(int3(sourcePixel, 0)).xyz);
    float4 clipPosition = float4(
        (float(sourcePixel.x) + 0.5f) / float(fullSize.x) * 2.0f - 1.0f,
        -((float(sourcePixel.y) + 0.5f) / float(fullSize.y) * 2.0f - 1.0f),
        depth,
        1.0f);
    float4 cameraRelativeWorldPositionH = mul(clipPosition, camera.invViewProjection);
    float3 cameraRelativeWorldPosition = cameraRelativeWorldPositionH.xyz / cameraRelativeWorldPositionH.w;
    float3 rayDirection = BuildHemisphereDirection(normal, Hash12(pixel, frameIndex));

    RayDesc ray;
    ray.Origin = cameraRelativeWorldPosition + normal * bias;
    ray.Direction = rayDirection;
    ray.TMin = 0.001f;
    ray.TMax = radius;

    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER> query;
    query.TraceRayInline(sceneTlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, 0xFF, ray);
    query.Proceed();
    float occlusion = query.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? saturate(1.0f - strength) : 1.0f;
    outputTexture[pixel] = float4(occlusion, occlusion, occlusion, 1.0f);
}
