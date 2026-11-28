// Vertex wind shared by every pass that rasterises meshes, so the G-buffer, shadow maps and transparent forward
// pass move a vertex the same way. See docs/foliage.md, Wind.
//
// The global state arrives as two float4 values per frame, written by WindFrameConstants in C#:
//   directionStrength = (direction x, direction z, strength, gust strength)
//   gustParams        = (gust frequency, 1 / gust world scale, time in seconds, unused)
// Passes that write motion vectors receive the previous frame's pair as well and evaluate the previous position
// with it. A strength of zero turns wind off.
//
// Materials choose a mode and two scales, carried in GpuMaterialData.windMode and windScales (two halves:
// strength scale, flutter scale). The pack's meshes reach the engine without vertex colours, so per-vertex
// weights are derived from the vertex's position relative to its instance origin: height above the origin bends
// the whole plant, and distance from the vertical axis through the origin weights branch and leaf flutter.

import "draw_data.slang";

static const uint WIND_MODE_NONE = 0u;
static const uint WIND_MODE_TREE = 1u;
static const uint WIND_MODE_GRASS = 2u;

// Metres of tip displacement per unit strength, times height squared.
static const float WIND_TREE_BEND_PER_SQUARE_METRE = 0.0025f;
static const float WIND_GRASS_BEND_PER_SQUARE_METRE = 0.35f;
// Metres of leaf flutter per unit strength at full weight.
static const float WIND_TREE_FLUTTER_METRES = 0.04f;
static const float WIND_GRASS_FLUTTER_METRES = 0.015f;
// Distance from the trunk axis, in metres, at which flutter reaches full weight.
static const float WIND_TREE_FLUTTER_RADIUS = 3.0f;

struct WindFrame
{
    float2 direction;
    float strength;
    float gustStrength;
    float gustFrequency;
    float inverseGustScale;
    float time;
};

WindFrame MakeWindFrame(float4 directionStrength, float4 gustParams)
{
    WindFrame wind;
    wind.direction = directionStrength.xy;
    wind.strength = directionStrength.z;
    wind.gustStrength = directionStrength.w;
    wind.gustFrequency = gustParams.x;
    wind.inverseGustScale = gustParams.y;
    wind.time = gustParams.z;
    return wind;
}

float WindHash(float2 cell)
{
    float3 p = frac(float3(cell.xyx) * 0.1031f);
    p += dot(p, p.yzx + 33.33f);
    return frac((p.x + p.y) * p.z);
}

// Smooth value noise in [0, 1]: the scrolling gust field.
float WindGustNoise(float2 position)
{
    float2 cell = floor(position);
    float2 fraction = position - cell;
    float2 blend = fraction * fraction * (3.0f - 2.0f * fraction);
    float a = WindHash(cell);
    float b = WindHash(cell + float2(1.0f, 0.0f));
    float c = WindHash(cell + float2(0.0f, 1.0f));
    float d = WindHash(cell + float2(1.0f, 1.0f));
    return lerp(lerp(a, b, blend.x), lerp(c, d, blend.x), blend.y);
}

uint GetMaterialWindMode(GpuMaterialData material)
{
    return material.windMode;
}

float2 GetMaterialWindScales(GpuMaterialData material)
{
    return float2(f16tof32(material.windScales & 0xFFFFu), f16tof32(material.windScales >> 16));
}

/// World-space offset of a vertex at rest position worldPosition, on an instance whose origin is instanceOrigin.
/// Zero for a material without wind or while the wind's strength is zero.
float3 EvaluateWindOffset(float3 worldPosition, float3 instanceOrigin, uint mode, float2 scales, WindFrame wind)
{
    if (mode == WIND_MODE_NONE || wind.strength <= 0.0f)
    {
        return float3(0.0f, 0.0f, 0.0f);
    }

    float3 relative = worldPosition - instanceOrigin;
    float height = max(relative.y, 0.0f);
    float phase = dot(instanceOrigin.xz, float2(0.37f, 0.71f));

    // The gust field scrolls downwind, so gusts roll across a forest or a field rather than pulsing in place.
    float2 gustPosition = instanceOrigin.xz * wind.inverseGustScale - wind.direction * (wind.time * wind.gustFrequency);
    float gust = WindGustNoise(gustPosition);
    float push = wind.strength * scales.x * (0.6f + wind.gustStrength * gust);

    float2 across = float2(-wind.direction.y, wind.direction.x);
    float3 offset = float3(0.0f, 0.0f, 0.0f);
    if (mode == WIND_MODE_TREE)
    {
        float bend = WIND_TREE_BEND_PER_SQUARE_METRE * height * height;
        float sway = push * (1.0f + 0.25f * sin(wind.time * 1.3f + phase));
        float swayAcross = push * 0.2f * sin(wind.time * 0.9f + phase * 1.7f);
        float2 bendOffset = (wind.direction * sway + across * swayAcross) * bend;
        offset.xz = bendOffset;
        // Shortens the bent height so a swaying trunk keeps its length rather than stretching.
        offset.y = -0.5f * dot(bendOffset, bendOffset) / max(height, 0.5f);

        float radial = length(relative.xz);
        float flutterWeight = saturate(radial / WIND_TREE_FLUTTER_RADIUS) * saturate(height * 0.5f);
        float3 vertexPhase = worldPosition * 1.7f;
        float flutterTime = wind.time * (6.0f + 2.0f * gust);
        float3 flutter = float3(
            sin(flutterTime + vertexPhase.x + vertexPhase.y),
            0.5f * sin(flutterTime * 1.3f + vertexPhase.z),
            cos(flutterTime * 1.1f + vertexPhase.x - vertexPhase.z));
        offset += flutter * (WIND_TREE_FLUTTER_METRES * scales.y * push * flutterWeight);
    }
    else
    {
        float bend = WIND_GRASS_BEND_PER_SQUARE_METRE * height * height;
        float sway = push * (0.8f + 0.4f * sin(wind.time * 2.1f + phase));
        float2 bendOffset = wind.direction * (sway * bend);
        offset.xz = bendOffset;
        offset.y = -0.5f * dot(bendOffset, bendOffset) / max(height, 0.05f);

        float flutter = sin(wind.time * 8.0f + phase * 3.1f + worldPosition.y * 4.0f);
        offset.xz += across * (flutter * WIND_GRASS_FLUTTER_METRES * scales.y * push * saturate(height * 2.0f));
    }

    return offset;
}

/// The translation row of a row-major object-to-world matrix: the instance origin wind bends around.
float3 GetWindInstanceOrigin(row_major float4x4 world)
{
    return float3(world[3][0], world[3][1], world[3][2]);
}
