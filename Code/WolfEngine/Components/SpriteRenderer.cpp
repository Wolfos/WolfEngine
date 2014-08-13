/*
WolfEngine © 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "SpriteRenderer.h"
#include "../Rendering/Screen.h"
#include "../ECS/GameObject.h"
#include "../Utilities/Debug.h"
#include "../Rendering/Image.h"

void SpriteRenderer::Added()
{
	center = new SDL_Point;
}

void SpriteRenderer::Update()
{
	if (layer >= Screen::layers)
	{
		Screen::layers = layer + 1;
	}
}

void SpriteRenderer::Render()
{
	SDL_Rect* rect = new SDL_Rect;
	rect->w = frameWidth;
	rect->h = frameHeight;

	SDL_QueryTexture(spriteSheet, 0, 0, &sheetwidth, &sheetheight);
	if (frameWidth != 0) sheetwidth /= frameWidth;
	if (frameHeight != 0) sheetheight /= frameHeight;

	clip = (SDL_Rect*)calloc((sheetwidth*sheetheight) + 2 * sheetwidth, sizeof(SDL_Rect));

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

	SDL_Rect* dst = new SDL_Rect;
	if (!gameObject->transform->ignoreCam)
	{
		dst->x = gameObject->transform->position.x - Screen::mainCamera->gameObject->transform->position.x;
		dst->y = gameObject->transform->position.y - Screen::mainCamera->gameObject->transform->position.y;
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

	SDL_RenderCopyEx(Screen::mainCamera->screen, spriteSheet, rect, dst, gameObject->transform->angle, center, SDL_FLIP_NONE);
	free(clip);
	delete(dst);
	delete(rect);
}


void SpriteRenderer::Load(std::string filename)
{
	spriteSheet = Image::Load(filename);
}