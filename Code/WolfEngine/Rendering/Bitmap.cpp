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
			textureID = cache[i]->textureID;
			size = cache[i]->size;
		}
	}

	if (!cached)
	{
		count = new int;

		SDL_Surface* surface = LoadSurface(file);
		size = {surface->w, surface->h};
		GenTexture(surface);
		SDL_FreeSurface(surface);

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
		//SDL_DestroyTexture(texture);
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


SDL_Surface* Bitmap::LoadSurface(std::string filename)
{
	std::string path = WolfEngine::FindAssetFolder() + "Sprites/" + filename;


	//Load image at specified path
	SDL_Surface *surface = IMG_Load(path.c_str());
	if (!surface)
	{
		printf("Unable to load image %s! SDL_image Error: %s\n", path.c_str(), IMG_GetError());
	}

	return surface;
}

void Bitmap::GenTexture(SDL_Surface* surface)
{
	glGenTextures(1, &textureID);
	glBindTexture(GL_TEXTURE_2D, textureID);

	int mode = GL_RGB;

	if(surface->format->BytesPerPixel == 4)
	{
		mode = GL_RGBA;
	}

	glTexImage2D(GL_TEXTURE_2D, 0, mode, size.x, size.y, 0, mode, GL_UNSIGNED_BYTE, surface->pixels);

	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_BORDER);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_BORDER);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
}
