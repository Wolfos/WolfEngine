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
	/*
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

	Vector3 objPos = gameObject->transform->GetPosition();
	Vector3 camPos = WolfEngine::scene->camera->gameObject->transform->GetPosition();
	Vector3 targetPos = objPos - camPos;
	dst->x = targetPos.x;
	dst->y = targetPos.y;

	dst->w = (int)(frameWidth*gameObject->transform->localScale.x);
	dst->h = (int)(frameHeight*gameObject->transform->localScale.y);

	center->x = (int)((frameWidth*gameObject->transform->localScale.x) / 2);
	center->y = (int)((frameHeight*gameObject->transform->localScale.y) / 2);

	spriteSheet->Blit(rect, dst, gameObject->transform->angle, center);

	free(clip);
	delete(dst);
	delete(rect);
	 */
}


void SpriteRenderer::Load(std::string filename)
{
	spriteSheet = new Bitmap(filename);
    frameWidth = spriteSheet->size.x;
    frameHeight = spriteSheet->size.y;
}
