// Shared particle and emitter layouts. These mirror WolfEngine/Vfx/VfxGpuTypes.cs field for field.
//
// Only four-component fields are used: a float3 is 16-byte aligned on Metal but not in D3D12 structured buffers,
// so a float3 member would shift every field after it on one backend.

static const uint VFX_FLAG_LOCAL_SPACE = 1u << 0;
static const uint VFX_FLAG_EMIT_FROM_EDGE = 1u << 1;
static const uint VFX_FLAG_ALIGN_GROUND = 1u << 2;
static const uint VFX_FLAG_ALIGN_VELOCITY = 1u << 3;
static const uint VFX_FLAG_SHEET_RANDOM_START = 1u << 4;
static const uint VFX_FLAG_RIBBON = 1u << 5;

static const uint VFX_SHAPE_POINT = 0u;
static const uint VFX_SHAPE_SPHERE = 1u;
static const uint VFX_SHAPE_BOX = 2u;
static const uint VFX_SHAPE_CIRCLE = 3u;

static const uint VFX_CURVE_SAMPLES = 16u;
static const uint VFX_NO_TEXTURE = 0xFFFFFFFFu;

struct GpuVfxEmitter
{
    row_major float4x4 localToOrigin;
    float4 origin;
    float4 shapeSize;
    float4 shapeOffset;
    float4 velocityGravity;
    float4 lifetimeSpeed;
    float4 sizeRotation;
    float4 angularDrag;
    // Velocity stretch, intensity, soft particle distance, ribbon taper pivot.
    float4 render;
    float4 startColor;
    float4 startColorVariant;
    float4 spriteSheet;
    uint flags;
    uint shape;
    uint generation;
    uint pad0;
    uint textureHandle;
    uint samplerHandle;
    uint pad1;
    uint pad2;
    float4 sizeCurve[4];
    float4 colorCurve[16];
};

struct GpuVfxSpawnBatch
{
    uint emitterSlot;
    uint firstSpawn;
    uint count;
    uint seed;
    uint ringBase;
    uint ringCapacity;
    uint ringStart;
    uint pad0;
    float4 positionStartAge;
    float4 positionEndAge;
    float4 emitterVelocity;
    float4 rotation;
};

struct GpuVfxParticle
{
    float4 positionAge;
    float4 velocityLifetime;
    // Size, rotation, angular velocity.
    float4 shape;
    // Emitter slot in the low 16 bits and its generation in the high 16, packed start colour, random seed.
    uint emitterSlotAndGeneration;
    uint color;
    uint seed;
    uint pad0;
};

struct GpuVfxRibbonPoint
{
    float4 rootAlong;
    float4 tipPad;
};

uint VfxHash(uint value)
{
    // PCG output permutation. Cheap and well distributed, so consecutive particle indices look unrelated.
    uint state = value * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float VfxRandom(uint seed, uint channel)
{
    return float(VfxHash(seed ^ VfxHash(channel + 0x9E3779B9u)) & 0x00FFFFFFu) / 16777216.0f;
}

float VfxRandomRange(uint seed, uint channel, float2 range)
{
    return lerp(range.x, range.y, VfxRandom(seed, channel));
}

float VfxSampleSizeCurve(GpuVfxEmitter emitter, float t)
{
    float position = saturate(t) * float(VFX_CURVE_SAMPLES - 1u);
    uint index = min(uint(position), VFX_CURVE_SAMPLES - 2u);
    float blend = position - float(index);
    uint next = index + 1u;
    float a = emitter.sizeCurve[index >> 2u][index & 3u];
    float b = emitter.sizeCurve[next >> 2u][next & 3u];
    return lerp(a, b, blend);
}

float4 VfxSampleColorCurve(GpuVfxEmitter emitter, float t)
{
    float position = saturate(t) * float(VFX_CURVE_SAMPLES - 1u);
    uint index = min(uint(position), VFX_CURVE_SAMPLES - 2u);
    float blend = position - float(index);
    return lerp(emitter.colorCurve[index], emitter.colorCurve[index + 1u], blend);
}

float3 VfxRotate(float4 q, float3 v)
{
    float3 t = 2.0f * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

uint VfxPackColor(float4 color)
{
    uint4 bytes = uint4(round(saturate(color) * 255.0f));
    return bytes.x | (bytes.y << 8u) | (bytes.z << 16u) | (bytes.w << 24u);
}

float4 VfxUnpackColor(uint packed)
{
    return float4(
        float(packed & 0xFFu),
        float((packed >> 8u) & 0xFFu),
        float((packed >> 16u) & 0xFFu),
        float(packed >> 24u)) / 255.0f;
}

bool VfxParticleIsAlive(GpuVfxParticle particle)
{
    return particle.positionAge.w < particle.velocityLifetime.w;
}

uint VfxParticleEmitterSlot(GpuVfxParticle particle)
{
    return particle.emitterSlotAndGeneration & 0xFFFFu;
}

uint VfxParticleGeneration(GpuVfxParticle particle)
{
    return particle.emitterSlotAndGeneration >> 16u;
}
