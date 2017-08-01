/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "SpriteRenderer.h"
#include "../ECS/GameObject.h"
#include "../Utilities/Debug.h"
#include "../WolfEngine.h"

void SpriteRenderer::Added()
{
	center = new SDL_Point;
}

void SpriteRenderer::Update()
{
	if (layer >= WolfEngine::scene->layers)
	{
		WolfEngine::scene->layers = layer + 1;
	}
}

void SpriteRenderer::Render()
{
	WRect* rect = new WRect;
	rect->w = frameWidth;
	rect->h = frameHeight;

	sheetwidth = spriteSheet->size.x;
	sheetheight = spriteSheet->size.y;
	if (frameWidth != 0) sheetwidth /= frameWidth;
	if (frameHeight != 0) sheetheight /= frameHeight;

	clip = (WRect*)calloc((sheetwidth*sheetheight) + 2 * sheetwidth, sizeof(WRect));

	int i = 0;
	for (int y = 0; y <= sheetheight; y++)
	{
		for (int x = 0; x<sheetwidth; x++)
		{
			clip[i].x = x*frameWidth + (sheetOffset*x);
			clip[i].y = y*frameHeight + (sheetOffset*y);
			clip[i].w = frameWidth;
			clip[i].h = frameHeight;
			i++;
		}
	}
	rect->x = clip[frame].x;
	rect->y = clip[frame].y;

	WRect* dst = new WRect;
	if (!gameObject->transform->ignoreCam)
	{
		dst->x = gameObject->transform->position.x - WolfEngine::scene->camera->gameObject->transform->position.x;
		dst->y = gameObject->transform->position.y - WolfEngine::scene->camera->gameObject->transform->position.y;
	}
	else
	{
		dst->x = gameObject->transform->position.x;
		dst->y = gameObject->transform->position.y;
	}
	dst->w = (int)(frameWidth*gameObject->transform->scale.x);
	dst->h = (int)(frameHeight*gameObject->transform->scale.y);

	center->x = (int)((frameWidth*gameObject->transform->scale.x) / 2);
	center->y = (int)((frameHeight*gameObject->transform->scale.y) / 2);

	spriteSheet->Blit(rect, dst, gameObject->transform->angle, center);

	free(clip);
	delete(dst);
	delete(rect);
}


void SpriteRenderer::Load(std::string filename)
{
	spriteSheet = new Bitmap(filename);
}