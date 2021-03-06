/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#define _CRT_SECURE_NO_DEPRECATE
#include "Map.h"
#include "../Math/WolfMath.h"
#include "../Utilities/Debug.h"
#include "../Components/Transform.h"
#include "../Components/Camera.h"


Map::Map(int w, int h, int l, int defaultValue, float scale)
{
	width = w;
	height = h;
	layers = l;
	this->scale = scale;

	data = (int*)calloc(width*height*layers, sizeof(int));
	if (!data)
	{
		printf("Could not create a map with size %i x %i x %i", width, height, layers);
		return;
	}

	int i = 0;
	for (int l = 0; l < layers; l++)
	{
		for (int x = 0; x < width; x++)
		{
			for (int y = 0; y < height; y++)
			{
				data[i] = defaultValue;
				i++;
			}
		}
	}
}

void Map::Load(std::string filename)
{
	FILE *f = fopen(filename.c_str(), "rb");

	if (!f)
	{
		Debug::Log("Could not load %s", filename.c_str());
		return;
	}

	int w, h, l;

	fread(&w, sizeof(int), 1, f);
	fread(&h, sizeof(int), 1, f);
	fread(&l, sizeof(int), 1, f);

	layers = l;
	width = w;
	height = h;

	if (data) free(data);

	data = (int*)calloc(width*height*layers, sizeof(int));

	if (!data)
	{
		Debug::Log("Could not allocate enough memory to load the map");
		return;
	}

	for (int i = 0; i < width*height*layers; i++)
	{
		fread(&data[i], sizeof(int), 1, f);
	}

	fclose(f);
}

void Map::Write(std::string filename)
{
	FILE *f = fopen(filename.c_str(), "wb");
	if (!f)
	{
		Debug::Log("Error opening %s for writing", filename.c_str());
		return;
	}

	fwrite(&width, sizeof(int), 1, f);
	fwrite(&height, sizeof(int), 1, f);
	fwrite(&layers, sizeof(int), 1, f);
	fwrite(data, sizeof(int), width*height*layers, f);

	fclose(f);
}

int Map::Get(int x, int y, int l)
{
	int pos = x + (y*width);

	//Move to correct layer
	pos += (width*height)*l;

	int val = data[pos];

	return val;
}

void Map::Put(int x, int y, int l, int value)
{
	int pos = x + (y*width);

	//Move to correct layer
	pos += (width*height)*l;

	data[pos] = value;
}

void Map::Render(int layer, Bitmap* spritesheet,
	int tilewidth, int tileheight, int offset, GameObject* camera)
{
	//Tiles to start and finish render on
	int startX;
	int startY;
	int endX;
	int endY;

	WRect sourcerect;
	WRect targetrect;

	int sheetwidth = spritesheet->size.x;
	int sheetheight = spritesheet->size.y;

	sheetwidth /= tilewidth;
	sheetheight /= tileheight;

	sourcerect.w = tilewidth;
	sourcerect.h = tileheight;
	targetrect.w = tilewidth;
	targetrect.h = tileheight;


	//Occlusion culling, but need to make sure we CAN cull first
	//If we can't cull (ergo, the map is too small or we're off the map), we just render the whole map
    Vector3<> cameraPosition = camera->transform->GetPosition();
	if (cameraPosition.x >= 0)startX = cameraPosition.x / tilewidth;
	else startX = 0;

	if (cameraPosition.y >= 0)startY = cameraPosition.y / tileheight;
	else startY = 0;


	if (((cameraPosition.x + camera->GetComponent<Camera>()->width / scale) / tilewidth) + 1 <= width)
	{
		endX = ((cameraPosition.x + camera->GetComponent<Camera>()->width / scale) / tilewidth) + 1;
	}
	else endX = width;
	
	if (((cameraPosition.y + camera->GetComponent<Camera>()->height / scale) / tileheight) + 1 <= height)
	{
		endY = ((cameraPosition.y + camera->GetComponent<Camera>()->height / scale) / tileheight) + 1;
	}
	else endY = height;

	for (int y = startY; y<endY; y++)
	{
		for (int x = startX; x<endX; x++)
		{
			targetrect.x = x*tilewidth - cameraPosition.x;
			targetrect.y = y*tileheight - cameraPosition.y;

			int val = Get(x, y, layer);
			if (val<=sheetwidth*sheetheight && val>=0)
			{
				int yPos = WolfMath::Floor(val / sheetwidth);
				int xPos = val - yPos * sheetwidth;
				sourcerect.x = xPos * tilewidth;
				sourcerect.y = yPos * tileheight;

				//spritesheet->Blit(&sourcerect, &targetrect, 0, NULL, scale);
			}
		}
	}
}
