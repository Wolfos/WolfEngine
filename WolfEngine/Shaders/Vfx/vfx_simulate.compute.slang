// GPU particle simulation. Each emitter instance owns a ring of slots in the world's particle pool, chosen on the
// CPU, so neither kernel allocates: VfxSimulateCS ages and moves every slot, then VfxEmitCS writes this frame's new
// particles over the oldest slots of their rings.

import "vfx_common.slang";

RWStructuredBuffer<GpuVfxParticle> g_Particles : register(u0);
StructuredBuffer<GpuVfxEmitter> g_Emitters : register(t2);
StructuredBuffer<GpuVfxSpawnBatch> g_Spawns : register(t3);

cbuffer VfxSimulateParams : register(b11)
{
    float deltaTime;
    uint particleCount;
    uint emitterCount;
    uint spawnCount;
    uint batchCount;
    uint3 _pad0;
};

// Longest step integration takes at once. Longer frames, and particles catching up on time they spawned into, are
// split into steps no longer than this, so a hitch or a paused preview stepping forward follows the same arcs.
static const float VFX_MAX_STEP = 1.0f / 30.0f;

void Integrate(inout float3 position, inout float3 velocity, GpuVfxEmitter emitter, float duration)
{
    uint steps = max(uint(ceil(duration / VFX_MAX_STEP)), 1u);
    float step = duration / float(steps);
    for (uint i = 0u; i < steps; i++)
    {
        velocity.y -= emitter.velocityGravity.w * step;
        // Implicit form, so a large drag slows particles down instead of reversing them.
        velocity /= 1.0f + emitter.angularDrag.z * step;
        position += velocity * step;
    }
}

[numthreads(64, 1, 1)]
void VfxSimulateCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
    if (index >= particleCount)
    {
        return;
    }

    GpuVfxParticle particle = g_Particles[index];
    if (VfxParticleIsAlive(particle) == false)
    {
        return;
    }

    uint slot = VfxParticleEmitterSlot(particle);
    if (slot >= emitterCount || g_Emitters[slot].generation != VfxParticleGeneration(particle))
    {
        // Its emitter was stopped immediately or restarted; the slot may already belong to someone else.
        particle.positionAge.w = particle.velocityLifetime.w;
        g_Particles[index] = particle;
        return;
    }

    GpuVfxEmitter emitter = g_Emitters[slot];
    float3 position = particle.positionAge.xyz;
    float3 velocity = particle.velocityLifetime.xyz;
    Integrate(position, velocity, emitter, deltaTime);

    particle.positionAge = float4(position, particle.positionAge.w + deltaTime);
    particle.velocityLifetime.xyz = velocity;
    particle.shape.y += particle.shape.z * deltaTime;
    g_Particles[index] = particle;
}

uint FindBatch(uint spawnIndex)
{
    uint low = 0u;
    uint high = batchCount - 1u;
    while (low < high)
    {
        uint middle = (low + high + 1u) >> 1u;
        if (g_Spawns[middle].firstSpawn <= spawnIndex)
        {
            low = middle;
        }
        else
        {
            high = middle - 1u;
        }
    }

    return low;
}

// Offset from the emitter's centre and the outward direction particles leave the shape in, in emitter space.
void SampleShape(GpuVfxEmitter emitter, uint seed, out float3 offset, out float3 direction)
{
    float u = VfxRandom(seed, 10u);
    float v = VfxRandom(seed, 11u);
    float w = VfxRandom(seed, 12u);
    bool edge = (emitter.flags & VFX_FLAG_EMIT_FROM_EDGE) != 0u;

    float z = v * 2.0f - 1.0f;
    float angle = u * 6.28318530718f;
    float ring = sqrt(max(1.0f - z * z, 0.0f));
    float3 sphereDirection = float3(cos(angle) * ring, z, sin(angle) * ring);

    if (emitter.shape == VFX_SHAPE_SPHERE)
    {
        float radius = emitter.shapeSize.x * (edge ? 1.0f : pow(w, 1.0f / 3.0f));
        offset = sphereDirection * radius;
        direction = sphereDirection;
        return;
    }

    if (emitter.shape == VFX_SHAPE_BOX)
    {
        offset = (float3(u, v, w) * 2.0f - 1.0f) * emitter.shapeSize.xyz;
        float lengthSquared = dot(offset, offset);
        direction = lengthSquared > 1e-8f ? offset * rsqrt(lengthSquared) : sphereDirection;
        return;
    }

    if (emitter.shape == VFX_SHAPE_CIRCLE)
    {
        float3 flat = float3(cos(angle), 0.0f, sin(angle));
        offset = flat * emitter.shapeSize.x * (edge ? 1.0f : sqrt(w));
        direction = flat;
        return;
    }

    offset = float3(0.0f, 0.0f, 0.0f);
    direction = sphereDirection;
}

[numthreads(64, 1, 1)]
void VfxEmitCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint spawnIndex = dispatchThreadId.x;
    if (spawnIndex >= spawnCount || batchCount == 0u)
    {
        return;
    }

    GpuVfxSpawnBatch batch = g_Spawns[FindBatch(spawnIndex)];
    uint local = spawnIndex - batch.firstSpawn;
    if (local >= batch.count || batch.emitterSlot >= emitterCount)
    {
        return;
    }

    GpuVfxEmitter emitter = g_Emitters[batch.emitterSlot];
    uint seed = VfxHash(batch.seed + local * 0x68E31DA4u);

    // Particles are spread evenly over the time and path the emitter covered while spawning this batch.
    float fraction = (float(local) + 0.5f) / float(batch.count);
    float age = lerp(batch.positionStartAge.w, batch.positionEndAge.w, fraction);
    float3 basePosition = lerp(batch.positionStartAge.xyz, batch.positionEndAge.xyz, fraction);

    float3 offset;
    float3 direction;
    SampleShape(emitter, seed, offset, direction);
    offset += emitter.shapeOffset.xyz;
    float speed = VfxRandomRange(seed, 20u, emitter.lifetimeSpeed.zw);
    float3 velocity = direction * speed + emitter.velocityGravity.xyz;

    float3 position;
    if ((emitter.flags & VFX_FLAG_LOCAL_SPACE) != 0u)
    {
        position = offset;
    }
    else
    {
        position = basePosition + VfxRotate(batch.rotation, offset);
        velocity = VfxRotate(batch.rotation, velocity) + batch.emitterVelocity.xyz * emitter.angularDrag.w;
    }

    float lifetime = VfxRandomRange(seed, 21u, emitter.lifetimeSpeed.xy);
    float rotation = VfxRandomRange(seed, 22u, emitter.sizeRotation.zw);
    float angularVelocity = VfxRandomRange(seed, 23u, emitter.angularDrag.xy);

    // Catch up on the part of the frame the particle already lived through.
    Integrate(position, velocity, emitter, age);

    GpuVfxParticle particle;
    particle.positionAge = float4(position, age);
    particle.velocityLifetime = float4(velocity, lifetime);
    particle.shape = float4(
        VfxRandomRange(seed, 24u, emitter.sizeRotation.xy),
        rotation + angularVelocity * age,
        angularVelocity,
        0.0f);
    particle.emitterSlotAndGeneration = (batch.emitterSlot & 0xFFFFu) | (emitter.generation << 16u);
    particle.color = VfxPackColor(lerp(emitter.startColor, emitter.startColorVariant, VfxRandom(seed, 25u)));
    particle.seed = seed;
    particle.pad0 = 0u;

    uint ringIndex = (batch.ringStart + local) % max(batch.ringCapacity, 1u);
    g_Particles[batch.ringBase + ringIndex] = particle;
}
