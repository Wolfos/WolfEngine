// Copyright 2020-2022 Matias N. Goldberg
// Derived from Betsy's bc4.glsl.
// Ported to Slang for WolfEngine's offline GPU texture import path. See LICENSE.md alongside this file.

cbuffer Params : register(b11)
{
	uint g_width;
	uint g_height;
	uint g_blocksX;
	uint g_channelIndex;
}

RWStructuredBuffer<uint> g_sourcePixels : register(u0);
RWStructuredBuffer<uint2> g_outputBlocks : register(u1);

groupshared float2 g_minMaxValues[4u * 4u * 4u];
groupshared uint g_maskLo[4u * 4u];
groupshared uint g_maskHi[4u * 4u];

uint LoadPixel(uint2 pixelCoord)
{
	uint x = min(pixelCoord.x, g_width - 1);
	uint y = min(pixelCoord.y, g_height - 1);
	return g_sourcePixels[y * g_width + x];
}

float4 UnpackUnorm4x8Compat(uint packed)
{
	return float4(
		float((packed >> 0u) & 0xFFu),
		float((packed >> 8u) & 0xFFu),
		float((packed >> 16u) & 0xFFu),
		float((packed >> 24u) & 0xFFu)) * (1.0 / 255.0);
}

uint PackUnorm4x8Compat(float4 value)
{
	uint4 bytes = uint4(round(saturate(value) * 255.0));
	return
		(bytes.x << 0u) |
		(bytes.y << 8u) |
		(bytes.z << 16u) |
		(bytes.w << 24u);
}

[numthreads(4, 4, 4)]
void CompressBc4(
	uint3 groupThreadId : SV_GroupThreadID,
	uint3 dispatchThreadId : SV_DispatchThreadID)
{
	const uint blockThreadId = groupThreadId.x;
	const uint2 blockCoord = dispatchThreadId.yz;
	const uint blocksY = max(1u, (g_height + 3u) / 4u);
	const bool activeBlock = blockCoord.x < g_blocksX && blockCoord.y < blocksY;
	const uint2 pixelsToLoadBase = blockCoord << 2u;

	float4 srcPixel = 0.0;
	[unroll]
	for (uint i = 0u; i < 4u; ++i)
	{
		const uint2 pixelsToLoad = pixelsToLoadBase + uint2(i, blockThreadId);
		const float4 value = UnpackUnorm4x8Compat(LoadPixel(pixelsToLoad));
		srcPixel[i] = (
			g_channelIndex == 0u ? value.x :
			g_channelIndex == 1u ? value.y :
			g_channelIndex == 2u ? value.z :
			value.w) * 255.0;
	}

	float minVal = min(min(srcPixel.x, srcPixel.y), min(srcPixel.z, srcPixel.w));
	float maxVal = max(max(srcPixel.x, srcPixel.y), max(srcPixel.z, srcPixel.w));

	const uint minMaxIdxBase = (groupThreadId.z << 4u) + (groupThreadId.y << 2u);
	const uint maskIdxBase = (groupThreadId.z << 2u) + groupThreadId.y;
	g_minMaxValues[minMaxIdxBase + blockThreadId] = float2(minVal, maxVal);
	g_maskLo[maskIdxBase] = 0u;
	g_maskHi[maskIdxBase] = 0u;

	GroupMemoryBarrierWithGroupSync();

	[unroll]
	for (uint i = 0u; i < 4u; ++i)
	{
		minVal = min(g_minMaxValues[minMaxIdxBase + i].x, minVal);
		maxVal = max(g_minMaxValues[minMaxIdxBase + i].y, maxVal);
	}

	float dist = maxVal - minVal;
	float dist4 = dist * 4.0;
	float dist2 = dist * 2.0;
	float bias = dist < 8.0 ? (dist - 1.0) : (trunc(dist * 0.5) + 2.0);
	bias -= minVal * 7.0;

	uint mask0 = 0u;
	uint mask1 = 0u;
	[unroll]
	for (uint i = 0u; i < 4u; ++i)
	{
		float a = srcPixel[i] * 7.0 + bias;
		int ind = 0;
		if (a >= dist4)
		{
			ind = 4;
			a -= dist4;
		}

		if (a >= dist2)
		{
			ind += 2;
			a -= dist2;
		}

		if (a >= dist)
		{
			ind += 1;
		}

		ind = (-ind) & 7;
		ind ^= (2 > ind) ? 1 : 0;

		const uint bits = 16u + ((blockThreadId << 2u) + i) * 3u;
		if (bits < 32u)
		{
			mask0 |= uint(ind) << bits;
			if (bits + 3u > 32u)
			{
				mask1 |= uint(ind) >> (32u - bits);
			}
		}
		else
		{
			mask1 |= uint(ind) << (bits - 32u);
		}
	}

	if (mask0 != 0u)
	{
		InterlockedOr(g_maskLo[maskIdxBase], mask0);
	}

	if (mask1 != 0u)
	{
		InterlockedOr(g_maskHi[maskIdxBase], mask1);
	}

	GroupMemoryBarrierWithGroupSync();

	if (blockThreadId == 0u && activeBlock)
	{
		uint2 outputBytes;
		outputBytes.x = PackUnorm4x8Compat(float4(maxVal / 255.0, minVal / 255.0, 0.0, 0.0));
		outputBytes.x |= g_maskLo[maskIdxBase];
		outputBytes.y = g_maskHi[maskIdxBase];
		g_outputBlocks[blockCoord.y * g_blocksX + blockCoord.x] = outputBytes;
	}
}
