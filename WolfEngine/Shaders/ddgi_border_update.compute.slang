import "common_bindless.slang";
import "ddgi_common.slang";

uint2 ClampToInterior(uint2 pixel, uint interiorSize)
{
    uint tileSize = interiorSize + tileBorderSize * 2u;
    uint2 tile = pixel / tileSize;
    uint2 local = pixel - tile * tileSize;
    local = clamp(local, tileBorderSize.xx, (tileBorderSize + interiorSize - 1u).xx);
    return tile * tileSize + local;
}

void UpdateBorder(uint2 pixel, uint width, uint height, uint interiorSize, uint handle)
{
    uint tileSize = interiorSize + tileBorderSize * 2u;
    uint2 tile = pixel / tileSize;
    if (tile.x >= atlasColumns || tile.y >= atlasRows)
    {
        return;
    }

    uint probeIndex = tile.y * atlasColumns + tile.x;
    if (probeIndex >= probeCount)
    {
        return;
    }

    uint2 local = pixel - tile * tileSize;
    bool border = local.x < tileBorderSize || local.y < tileBorderSize ||
                  local.x >= tileBorderSize + interiorSize || local.y >= tileBorderSize + interiorSize;
    if (!border)
    {
        return;
    }

    uint2 sourcePixel = ClampToInterior(pixel, interiorSize);
    RWTexture2D<float4> texture = GetRWTexture(handle);
    texture[pixel] = texture[sourcePixel];
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadId.xy;
    if (pixel.x < irradianceAtlasWidth && pixel.y < irradianceAtlasHeight)
    {
        UpdateBorder(pixel, irradianceAtlasWidth, irradianceAtlasHeight, irradianceTileInteriorSize, irradianceHistoryWriteHandle);
    }

    if (pixel.x < visibilityAtlasWidth && pixel.y < visibilityAtlasHeight)
    {
        UpdateBorder(pixel, visibilityAtlasWidth, visibilityAtlasHeight, visibilityTileInteriorSize, visibilityHistoryWriteHandle);
    }
}
