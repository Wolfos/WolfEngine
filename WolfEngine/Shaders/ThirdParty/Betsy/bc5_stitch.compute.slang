// Copyright 2020-2022 Matias N. Goldberg
// Derived from Betsy's etc2_rgba_stitch.glsl and BC4 composition flow used for BC5.
// Ported to Slang for WolfEngine's offline GPU texture import path. See Shaders/betsy/LICENSE.md.

cbuffer Params : register(b11)
{
	uint g_blocksX;
	uint g_blocksY;
	uint g_reserved0;
	uint g_reserved1;
}

RWStructuredBuffer<uint2> g_redBlocks : register(u0);
RWStructuredBuffer<uint2> g_greenBlocks : register(u1);
RWStructuredBuffer<uint4> g_outputBlocks : register(u2);

[numthreads(8, 8, 1)]
void StitchBc5(uint3 dispatchThreadId : SV_DispatchThreadID)
{
	uint2 blockCoord = dispatchThreadId.xy;
	if (blockCoord.x >= g_blocksX || blockCoord.y >= g_blocksY)
	{
		return;
	}

	uint blockIndex = blockCoord.y * g_blocksX + blockCoord.x;
	uint2 red = g_redBlocks[blockIndex];
	uint2 green = g_greenBlocks[blockIndex];
	g_outputBlocks[blockIndex] = uint4(red.xy, green.xy);
}
