// Copyright 2020-2022 Matias N. Goldberg
// Derived from Betsy's bc1.glsl / BC1_tables.inl, which in turn includes code adapted from stb_dxt.
// Ported to Slang for WolfEngine's offline GPU texture import path. See LICENSE.md alongside this file.

cbuffer Params : register(b11)
{
	uint g_width;
	uint g_height;
	uint g_blocksX;
	uint g_numRefinements;
}

RWStructuredBuffer<uint> g_sourcePixels : register(u0);
RWStructuredBuffer<uint2> g_outputBlocks : register(u1);
RWStructuredBuffer<float2> g_match5 : register(u2);
RWStructuredBuffer<float2> g_match6 : register(u3);

static const float FLT_MAX_VALUE = 340282346638528859811704183484516925440.0;

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

float3 Rgb565To888(float rgb565)
{
	float3 retVal;
	retVal.x = floor(rgb565 / 2048.0);
	retVal.y = floor(fmod(rgb565, 2048.0) / 32.0);
	retVal.z = floor(fmod(rgb565, 32.0));
	return floor(retVal * float3(8.25, 4.0625, 8.25));
}

float Rgb888To565(float3 rgbValue)
{
	rgbValue.rb = floor(rgbValue.rb * 31.0 / 255.0 + 0.5);
	rgbValue.g = floor(rgbValue.g * 63.0 / 255.0 + 0.5);
	return rgbValue.r * 2048.0 + rgbValue.g * 32.0 + rgbValue.b;
}

float3 Lerp13(float3 a, float3 b)
{
	return floor((2.0 * a + b) / 3.0);
}

void EvalColors(out float3 colours[4], float c0, float c1)
{
	colours[0] = Rgb565To888(c0);
	colours[1] = Rgb565To888(c1);
	colours[2] = Lerp13(colours[0], colours[1]);
	colours[3] = Lerp13(colours[1], colours[0]);
}

void OptimizeColorsBlock(uint srcPixelsBlock[16], out float outMinEndp16, out float outMaxEndp16)
{
	float3 avgColour = UnpackUnorm4x8Compat(srcPixelsBlock[0]).xyz;
	float3 minColour = avgColour;
	float3 maxColour = avgColour;

	for (int i = 1; i < 16; ++i)
	{
		const float3 currColourUnorm = UnpackUnorm4x8Compat(srcPixelsBlock[i]).xyz;
		avgColour += currColourUnorm;
		minColour = min(minColour, currColourUnorm);
		maxColour = max(maxColour, currColourUnorm);
	}

	avgColour = round(avgColour * 255.0 / 16.0);
	maxColour *= 255.0;
	minColour *= 255.0;

	float cov[6];
	[unroll]
	for (int i = 0; i < 6; ++i)
	{
		cov[i] = 0.0;
	}

	for (int i = 0; i < 16; ++i)
	{
		const float3 currColour = UnpackUnorm4x8Compat(srcPixelsBlock[i]).xyz * 255.0;
		float3 rgbDiff = currColour - avgColour;
		cov[0] += rgbDiff.r * rgbDiff.r;
		cov[1] += rgbDiff.r * rgbDiff.g;
		cov[2] += rgbDiff.r * rgbDiff.b;
		cov[3] += rgbDiff.g * rgbDiff.g;
		cov[4] += rgbDiff.g * rgbDiff.b;
		cov[5] += rgbDiff.b * rgbDiff.b;
	}

	[unroll]
	for (int i = 0; i < 6; ++i)
	{
		cov[i] /= 255.0;
	}

	float3 vF = maxColour - minColour;
	[unroll]
	for (int iter = 0; iter < 4; ++iter)
	{
		const float r = vF.r * cov[0] + vF.g * cov[1] + vF.b * cov[2];
		const float g = vF.r * cov[1] + vF.g * cov[3] + vF.b * cov[4];
		const float b = vF.r * cov[2] + vF.g * cov[4] + vF.b * cov[5];
		vF = float3(r, g, b);
	}

	const float magn = max(abs(vF.r), max(abs(vF.g), abs(vF.b)));
	float3 v;
	if (magn < 4.0)
	{
		v = float3(299.0, 587.0, 114.0);
	}
	else
	{
		v = trunc(vF * (512.0 / magn));
	}

	float3 minEndpoint = 0.0;
	float3 maxEndpoint = 0.0;
	float minDot = FLT_MAX_VALUE;
	float maxDot = -FLT_MAX_VALUE;
	for (int i = 0; i < 16; ++i)
	{
		const float3 currColour = UnpackUnorm4x8Compat(srcPixelsBlock[i]).xyz * 255.0;
		const float dotValue = dot(currColour, v);
		if (dotValue < minDot)
		{
			minDot = dotValue;
			minEndpoint = currColour;
		}

		if (dotValue > maxDot)
		{
			maxDot = dotValue;
			maxEndpoint = currColour;
		}
	}

	outMinEndp16 = Rgb888To565(minEndpoint);
	outMaxEndp16 = Rgb888To565(maxEndpoint);
}

uint MatchColorsBlock(uint srcPixelsBlock[16], float3 colour[4])
{
	uint mask = 0u;
	float3 dir = colour[0] - colour[1];
	float stops[4];
	[unroll]
	for (int i = 0; i < 4; ++i)
	{
		stops[i] = dot(colour[i], dir);
	}

	float c0Point = trunc((stops[1] + stops[3]) * 0.5);
	float halfPoint = trunc((stops[3] + stops[2]) * 0.5);
	float c3Point = trunc((stops[2] + stops[0]) * 0.5);

	for (uint i = 16u; i-- > 0u;)
	{
		const float3 currColour = UnpackUnorm4x8Compat(srcPixelsBlock[i]).xyz * 255.0;
		const float dotValue = dot(currColour, dir);
		mask <<= 2u;
		if (dotValue < halfPoint)
		{
			mask |= dotValue < c0Point ? 1u : 3u;
		}
		else
		{
			mask |= dotValue < c3Point ? 2u : 0u;
		}
	}

	return mask;
}

bool RefineBlock(uint srcPixelsBlock[16], uint mask, inout float inOutMinEndp16, inout float inOutMaxEndp16)
{
	float newMin16;
	float newMax16;
	const float oldMin = inOutMinEndp16;
	const float oldMax = inOutMaxEndp16;

	if ((mask ^ (mask << 2u)) < 4u)
	{
		float3 rgbVal = float3(8.0 / 255.0, 8.0 / 255.0, 8.0 / 255.0);
		for (int i = 0; i < 16; ++i)
		{
			rgbVal += UnpackUnorm4x8Compat(srcPixelsBlock[i]).xyz;
		}

		rgbVal = floor(rgbVal * (255.0 / 16.0));
		const uint3 rgbIndex = uint3(rgbVal);
		newMax16 =
			g_match5[rgbIndex.r][0] * 2048.0 +
			g_match6[rgbIndex.g][0] * 32.0 +
			g_match5[rgbIndex.b][0];
		newMin16 =
			g_match5[rgbIndex.r][1] * 2048.0 +
			g_match6[rgbIndex.g][1] * 32.0 +
			g_match5[rgbIndex.b][1];
	}
	else
	{
		const float w1Tab[4] = { 3.0, 0.0, 2.0, 1.0 };
		const float prods[4] = { 589824.0, 2304.0, 262402.0, 66562.0 };

		float akku = 0.0;
		uint cm = mask;
		float3 at1 = 0.0;
		float3 at2 = 0.0;
		for (int i = 0; i < 16; ++i, cm >>= 2u)
		{
			const float3 currColour = UnpackUnorm4x8Compat(srcPixelsBlock[i]).xyz * 255.0;
			const uint step = cm & 3u;
			const float w1 = w1Tab[step];
			akku += prods[step];
			at1 += currColour * w1;
			at2 += currColour;
		}

		at2 = 3.0 * at2 - at1;
		const float xx = floor(akku / 65535.0);
		const float yy = floor(fmod(akku, 65535.0) / 256.0);
		const float xy = fmod(akku, 256.0);

		float2 f_rb_g;
		f_rb_g.x = 3.0 * 31.0 / 255.0 / (xx * yy - xy * xy);
		f_rb_g.y = f_rb_g.x * 63.0 / 31.0;

		const float3 newMaxVal = clamp(
			floor((at1 * yy - at2 * xy) * f_rb_g.xyx + 0.5),
			0.0,
			float3(31.0, 63.0, 31.0));
		newMax16 = newMaxVal.x * 2048.0 + newMaxVal.y * 32.0 + newMaxVal.z;

		const float3 newMinVal = clamp(
			floor((at2 * xx - at1 * xy) * f_rb_g.xyx + 0.5),
			0.0,
			float3(31.0, 63.0, 31.0));
		newMin16 = newMinVal.x * 2048.0 + newMinVal.y * 32.0 + newMinVal.z;
	}

	inOutMinEndp16 = newMin16;
	inOutMaxEndp16 = newMax16;
	return oldMin != newMin16 || oldMax != newMax16;
}

[numthreads(8, 8, 1)]
void CompressBc1(uint3 dispatchThreadId : SV_DispatchThreadID)
{
	uint2 blockCoord = dispatchThreadId.xy;
	const uint blocksY = max(1u, (g_height + 3u) / 4u);
	if (blockCoord.x >= g_blocksX || blockCoord.y >= blocksY)
	{
		return;
	}

	uint srcPixelsBlock[16];
	bool allColoursEqual = true;
	const uint2 pixelBase = blockCoord << 2u;

	[unroll]
	for (uint i = 0u; i < 16u; ++i)
	{
		const uint2 pixelCoord = pixelBase + uint2(i & 3u, i >> 2u);
		srcPixelsBlock[i] = LoadPixel(pixelCoord);
		allColoursEqual = allColoursEqual && srcPixelsBlock[i] == srcPixelsBlock[0];
	}

	float maxEndp16;
	float minEndp16;
	uint mask = 0u;

	if (allColoursEqual)
	{
		const uint3 rgbVal = uint3(UnpackUnorm4x8Compat(srcPixelsBlock[0]).xyz * 255.0);
		mask = 0xAAAAAAAAu;
		maxEndp16 =
			g_match5[rgbVal.r][0] * 2048.0 +
			g_match6[rgbVal.g][0] * 32.0 +
			g_match5[rgbVal.b][0];
		minEndp16 =
			g_match5[rgbVal.r][1] * 2048.0 +
			g_match6[rgbVal.g][1] * 32.0 +
			g_match5[rgbVal.b][1];
	}
	else
	{
		OptimizeColorsBlock(srcPixelsBlock, minEndp16, maxEndp16);
		if (minEndp16 != maxEndp16)
		{
			float3 colours[4];
			EvalColors(colours, maxEndp16, minEndp16);
			mask = MatchColorsBlock(srcPixelsBlock, colours);
		}

		bool stopRefinement = false;
		for (uint i = 0u; i < g_numRefinements && !stopRefinement; ++i)
		{
			const uint lastMask = mask;
			if (RefineBlock(srcPixelsBlock, mask, minEndp16, maxEndp16))
			{
				if (minEndp16 != maxEndp16)
				{
					float3 colours[4];
					EvalColors(colours, maxEndp16, minEndp16);
					mask = MatchColorsBlock(srcPixelsBlock, colours);
				}
				else
				{
					mask = 0u;
					stopRefinement = true;
				}
			}

			stopRefinement = stopRefinement || mask == lastMask;
		}
	}

	if (maxEndp16 < minEndp16)
	{
		const float tmpValue = minEndp16;
		minEndp16 = maxEndp16;
		maxEndp16 = tmpValue;
		mask ^= 0x55555555u;
	}

	g_outputBlocks[blockCoord.y * g_blocksX + blockCoord.x] = uint2(
		uint(maxEndp16) | (uint(minEndp16) << 16u),
		mask);
}
