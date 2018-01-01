#include "Bitmap.h"

#include "../WolfEngine.h"

std::vector<Bitmap*> Bitmap::cache;

Bitmap::Bitmap(std::string file)
{
	bool cached = false;
	filename = file;
	for (size_t i = 0; i < cache.size(); i++)
	{
		if (cache[i]->filename == filename)
		{
			cached = true;
			cache[i]->count++;
			count = cache[i]->count;
			texture = cache[i]->texture;
			size = cache[i]->size;
		}
	}

	if (!cached)
	{
		count = new int;

		SDL_Surface* surface = LoadSurface(file);
		texture = ToTexture(surface);
		SDL_FreeSurface(surface);

		SDL_QueryTexture(texture, NULL, NULL, &size.x, &size.y);

		if (!texture)
		{
			printf("Unable to create texture from %s! SDL Error: %s\n", filename.c_str(), SDL_GetError());
		}
		cache.push_back(this);
	}

	rect = new WRect;

	rect->x = 0;
	rect->y = 0;
	rect->w = size.x;
	rect->h = size.y;
}

Bitmap::~Bitmap()
{
	*count--;
	if (*count <= 0)
	{
		SDL_DestroyTexture(texture);
		for (size_t i = 0; i < cache.size(); i++)
		{
			if (cache[i]->filename == filename)
			{
				cache.erase(cache.begin() + i);
				break;
			}
		}
		delete rect;
		delete count;
	}
}

void Bitmap::Blit(WRect* srcrect, WRect* dstrect, double angle, SDL_Point* center, float scale)
{
    bool deleteCenter = false;
	if (!center)
	{
		SDL_Point* tempcenter = new SDL_Point;
		tempcenter->x = size.x / 2;
		tempcenter->y = size.y / 2;
		center = tempcenter;
        deleteCenter = true;
	}
	WRect rect = *dstrect;
	rect.x *= scale;
	rect.y *= scale;
	rect.w *= scale;
	rect.h *= scale;
	SDL_RenderCopyEx(WolfEngine::renderer, texture, srcrect, &rect, angle, center, SDL_FLIP_NONE);
	if(deleteCenter) delete center;
}

SDL_Surface* Bitmap::LoadSurface(std::string filename)
{
	std::string path = WolfEngine::FindAssetFolder() + "Sprites/" + filename;


	//Load image at specified path
	SDL_Surface* loadedSurface = IMG_Load(path.c_str());
	if (!loadedSurface)
	{
		printf("Unable to load image %s! SDL_image Error: %s\n", path.c_str(), IMG_GetError());
	}

	return loadedSurface;
}

Texture* Bitmap::ToTexture(SDL_Surface* surface)
{
	return SDL_CreateTextureFromSurface(WolfEngine::renderer, surface);
}
