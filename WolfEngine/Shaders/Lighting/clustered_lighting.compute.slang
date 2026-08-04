import "../Common/common_lighting.slang";

cbuffer CameraParams : register(b11)
{
    row_major float4x4 invProjection;
    row_major float4x4 viewMatrix;
};

cbuffer ClusterParams : register(b12)
{
    uint clusterCountX;
    uint clusterCountY;
    uint clusterCountZ;
    uint clusterCount;
    uint framebufferWidth;
    uint framebufferHeight;
    float nearPlane;
    float farPlane;
    uint pointLightCount;
    uint3 _clusterPadding;
};

RWStructuredBuffer<PointLightData> g_PointLights : register(u0);
RWStructuredBuffer<ClusterAabb> g_ClusterAabbs : register(u1);
RWStructuredBuffer<ClusterHeader> g_ClusterHeaders : register(u2);
RWStructuredBuffer<uint> g_ClusterLightIndices : register(u3);
RWStructuredBuffer<uint> g_ClusterWriteCursor : register(u4);
RWStructuredBuffer<uint> g_ClusterOverflow : register(u5);

float ComputeSliceDepth(uint sliceIndex)
{
    float t = float(sliceIndex) / float(max(clusterCountZ, 1u));
    return nearPlane * pow(max(farPlane / max(nearPlane, 1e-5f), 1.0f), t);
}

float3 ComputeCornerRay(float2 ndc)
{
    float4 corner = mul(float4(ndc, 1.0f, 1.0f), invProjection);
    float3 ray = corner.xyz / max(corner.w, 1e-6f);
    return ray / max(ray.z, 1e-6f);
}

ClusterAabb BuildClusterAabb(uint clusterIndex)
{
    uint sliceStride = clusterCountX * clusterCountY;
    uint clusterZ = clusterIndex / sliceStride;
    uint rem = clusterIndex - (clusterZ * sliceStride);
    uint clusterY = rem / clusterCountX;
    uint clusterX = rem - (clusterY * clusterCountX);

    float x0 = (float(clusterX * CLUSTER_TILE_SIZE) / float(max(framebufferWidth, 1u))) * 2.0f - 1.0f;
    float x1 = (float(min((clusterX + 1u) * CLUSTER_TILE_SIZE, framebufferWidth)) / float(max(framebufferWidth, 1u))) * 2.0f - 1.0f;
    float y0 = 1.0f - (float(clusterY * CLUSTER_TILE_SIZE) / float(max(framebufferHeight, 1u))) * 2.0f;
    float y1 = 1.0f - (float(min((clusterY + 1u) * CLUSTER_TILE_SIZE, framebufferHeight)) / float(max(framebufferHeight, 1u))) * 2.0f;

    float zNear = ComputeSliceDepth(clusterZ);
    float zFar = ComputeSliceDepth(min(clusterZ + 1u, clusterCountZ));

    float3 rays[4];
    rays[0] = ComputeCornerRay(float2(x0, y0));
    rays[1] = ComputeCornerRay(float2(x1, y0));
    rays[2] = ComputeCornerRay(float2(x0, y1));
    rays[3] = ComputeCornerRay(float2(x1, y1));

    float3 minPoint = float3(1e30f, 1e30f, 1e30f);
    float3 maxPoint = float3(-1e30f, -1e30f, -1e30f);
    [unroll]
    for (uint i = 0u; i < 4u; ++i)
    {
        float3 nearPoint = rays[i] * zNear;
        float3 farPoint = rays[i] * zFar;
        minPoint = min(minPoint, min(nearPoint, farPoint));
        maxPoint = max(maxPoint, max(nearPoint, farPoint));
    }

    ClusterAabb result;
    result.minPoint = float4(minPoint, 0.0f);
    result.maxPoint = float4(maxPoint, 0.0f);
    return result;
}

bool IntersectsCluster(PointLightData light, ClusterAabb cluster)
{
    float3 center = light.viewPositionRange.xyz;
    float radius = max(light.viewPositionRange.w, 0.001f);
    float3 clamped = clamp(center, cluster.minPoint.xyz, cluster.maxPoint.xyz);
    float3 delta = center - clamped;
    return dot(delta, delta) <= radius * radius;
}

uint GetLinearClusterIndex(uint dispatchThreadIdX)
{
    return dispatchThreadIdX;
}

[numthreads(64, 1, 1)]
void CSBuildClusters(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint clusterIndex = GetLinearClusterIndex(dispatchThreadId.x);
    if (clusterIndex >= clusterCount)
    {
        return;
    }

    g_ClusterAabbs[clusterIndex] = BuildClusterAabb(clusterIndex);
    g_ClusterHeaders[clusterIndex].offset = clusterIndex * CLUSTER_MAX_LIGHTS_PER_CLUSTER;
    g_ClusterHeaders[clusterIndex].count = 0u;
    g_ClusterWriteCursor[clusterIndex] = 0u;
    if (clusterIndex < 2u)
    {
        g_ClusterOverflow[clusterIndex] = 0u;
    }
}

[numthreads(64, 1, 1)]
void CSWriteLightIndices(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint clusterIndex = GetLinearClusterIndex(dispatchThreadId.x);
    if (clusterIndex >= clusterCount)
    {
        return;
    }

    ClusterAabb cluster = g_ClusterAabbs[clusterIndex];
    uint writeCount = 0u;
    uint baseOffset = clusterIndex * CLUSTER_MAX_LIGHTS_PER_CLUSTER;
    for (uint i = 0u; i < pointLightCount; ++i)
    {
        if (IntersectsCluster(g_PointLights[i], cluster) == false)
        {
            continue;
        }

        if (writeCount < CLUSTER_MAX_LIGHTS_PER_CLUSTER)
        {
            g_ClusterLightIndices[baseOffset + writeCount] = i;
            writeCount++;
        }
        else
        {
            g_ClusterOverflow[0] = 1u;
        }
    }

    g_ClusterHeaders[clusterIndex].offset = baseOffset;
    g_ClusterHeaders[clusterIndex].count = writeCount;
}
