// Cross-backend triangle traversal. Metal's hardware is designed around the intersector API;
// Slang's portable RayQuery lowers to intersection_query there, which Apple documents as a
// higher-scratch compatibility path. Keep that platform distinction contained in this file.

struct WolfRayHit
{
    bool hit;
    float distance;
    uint instanceIndex;
    uint primitiveIndex;
    float2 barycentrics;
};

struct WolfRayAnyHit
{
    bool hit;
    float distance;
};

#if defined(WOLF_TARGET_METAL)
__target_intrinsic(metal, "WolfMetalTriangleHit")
struct WolfMetalTriangleHit
{
    bool HasHit()
    {
        __intrinsic_asm "(($0).type == metal::raytracing::intersection_type::triangle)";
    }

    float Distance()
    {
        __intrinsic_asm "($0).distance";
    }

    uint InstanceIndex()
    {
        __intrinsic_asm "($0).instance_id";
    }

    uint PrimitiveIndex()
    {
        __intrinsic_asm "($0).primitive_id";
    }

    float2 Barycentrics()
    {
        __intrinsic_asm "($0).triangle_barycentric_coord";
    }
};

__target_intrinsic(metal, "WolfMetalAnyHit")
struct WolfMetalAnyHit
{
    bool HasHit()
    {
        __intrinsic_asm "(($0).type != metal::raytracing::intersection_type::none)";
    }

    float Distance()
    {
        __intrinsic_asm "($0).distance";
    }
};

WolfMetalTriangleHit WolfTraceMetalClosest(
    RaytracingAccelerationStructure accelerationStructure,
    float3 origin,
    float3 direction,
    float minDistance,
    float maxDistance,
    uint mask)
{
    __requirePrelude(R"(
        using WolfMetalTriangleHit = metal::raytracing::intersection_result<
            metal::raytracing::triangle_data,
            metal::raytracing::instancing>;

        inline WolfMetalTriangleHit WolfTraceMetalClosestImpl(
            metal::raytracing::acceleration_structure<metal::raytracing::instancing> accelerationStructure,
            float3 origin,
            float3 direction,
            float minDistance,
            float maxDistance,
            uint mask)
        {
            metal::raytracing::intersector<
                metal::raytracing::triangle_data,
                metal::raytracing::instancing> triangleIntersector;
            triangleIntersector.assume_geometry_type(metal::raytracing::geometry_type::triangle);
            triangleIntersector.force_opacity(metal::raytracing::forced_opacity::opaque);
            metal::raytracing::ray ray(origin, direction, minDistance, maxDistance);
            return triangleIntersector.intersect(ray, accelerationStructure, mask);
        }
    )");
    __intrinsic_asm "WolfTraceMetalClosestImpl($0, $1, $2, $3, $4, $5)";
}

WolfMetalAnyHit WolfTraceMetalAny(
    RaytracingAccelerationStructure accelerationStructure,
    float3 origin,
    float3 direction,
    float minDistance,
    float maxDistance,
    uint mask)
{
    __requirePrelude(R"(
        using WolfMetalAnyHit = metal::raytracing::intersection_result<
            metal::raytracing::instancing>;

        inline WolfMetalAnyHit WolfTraceMetalAnyImpl(
            metal::raytracing::acceleration_structure<metal::raytracing::instancing> accelerationStructure,
            float3 origin,
            float3 direction,
            float minDistance,
            float maxDistance,
            uint mask)
        {
            metal::raytracing::intersector<metal::raytracing::instancing> triangleIntersector;
            triangleIntersector.assume_geometry_type(metal::raytracing::geometry_type::triangle);
            triangleIntersector.force_opacity(metal::raytracing::forced_opacity::opaque);
            triangleIntersector.accept_any_intersection(true);
            metal::raytracing::ray ray(origin, direction, minDistance, maxDistance);
            return triangleIntersector.intersect(ray, accelerationStructure, mask);
        }
    )");
    __intrinsic_asm "WolfTraceMetalAnyImpl($0, $1, $2, $3, $4, $5)";
}

WolfRayHit WolfConvertMetalHit(WolfMetalTriangleHit nativeHit, float missDistance)
{
    WolfRayHit result;
    result.hit = nativeHit.HasHit();
    result.distance = result.hit ? nativeHit.Distance() : missDistance;
    result.instanceIndex = result.hit ? nativeHit.InstanceIndex() : 0u;
    result.primitiveIndex = result.hit ? nativeHit.PrimitiveIndex() : 0u;
    result.barycentrics = result.hit ? nativeHit.Barycentrics() : 0.0f.xx;
    return result;
}

WolfRayAnyHit WolfConvertMetalAnyHit(WolfMetalAnyHit nativeHit, float missDistance)
{
    WolfRayAnyHit result;
    result.hit = nativeHit.HasHit();
    result.distance = result.hit ? nativeHit.Distance() : missDistance;
    return result;
}
#endif

WolfRayHit WolfTraceClosest(
    RaytracingAccelerationStructure accelerationStructure,
    RayDesc ray,
    uint mask)
{
#if defined(WOLF_TARGET_METAL)
    return WolfConvertMetalHit(WolfTraceMetalClosest(
        accelerationStructure,
        ray.Origin,
        ray.Direction,
        ray.TMin,
        ray.TMax,
        mask), ray.TMax);
#else
    RayQuery<RAY_FLAG_SKIP_CLOSEST_HIT_SHADER> query;
    query.TraceRayInline(accelerationStructure, RAY_FLAG_NONE, mask, ray);
    while (query.Proceed())
    {
    }

    WolfRayHit result;
    result.hit = query.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
    result.distance = result.hit ? query.CommittedRayT() : ray.TMax;
    result.instanceIndex = result.hit ? query.CommittedInstanceIndex() : 0u;
    result.primitiveIndex = result.hit ? query.CommittedPrimitiveIndex() : 0u;
    result.barycentrics = result.hit ? query.CommittedTriangleBarycentrics() : 0.0f.xx;
    return result;
#endif
}

WolfRayAnyHit WolfTraceAny(
    RaytracingAccelerationStructure accelerationStructure,
    RayDesc ray,
    uint mask)
{
#if defined(WOLF_TARGET_METAL)
    return WolfConvertMetalAnyHit(WolfTraceMetalAny(
        accelerationStructure,
        ray.Origin,
        ray.Direction,
        ray.TMin,
        ray.TMax,
        mask), ray.TMax);
#else
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER> query;
    query.TraceRayInline(
        accelerationStructure,
        RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
        mask,
        ray);
    // Proceed finalizes the committed result only when traversal returns false.
    while (query.Proceed())
    {
    }

    WolfRayAnyHit result;
    result.hit = query.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
    result.distance = result.hit ? query.CommittedRayT() : ray.TMax;
    return result;
#endif
}
