#include "Bitmap.h"
#include "../Game.h"

#ifdef __APPLE__
#include "CoreFoundation/CoreFoundation.h"
#endif

std::vector<Bitmap*> Bitmap::cache;

// TODO: Fix potential crash bug when loading the same image more than 2 times because the count isn't correct
// Maybe use pointers?
Bitmap::Bitmap(std::string file)
{
	bool cached = false;
	filename = file;
	for (int i = 0; i < cache.size(); i++)
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
}

Bitmap::~Bitmap()
{
	count--;
	if (count <= 0)
	{
		SDL_DestroyTexture(texture);
		for (int i = 0; i < cache.size(); i++)
		{
			if (cache[i]->filename == filename)
			{
				cache.erase(cache.begin() + i);
				break;
			}
		}
		delete count;
	}
}

void Bitmap::Blit(Rect* srcrect, Rect* dstrect, double angle, SDL_Point* center)
{
	if (!center)
	{
		SDL_Point* tempcenter = new SDL_Point;
		tempcenter->x = size.x / 2;
		tempcenter->y = size.y / 2;
		center = tempcenter;
	}
	SDL_RenderCopyEx(Game::renderer, texture, srcrect, dstrect, angle, center, SDL_FLIP_NONE);
	delete center;
}

SDL_Surface* Bitmap::LoadSurface(std::string filename)
{

#ifdef ANDROID
	std::string newFilename = "Sprites/";
	newFilename += filename;
#elif defined __APPLE__
	//Find the resources folder
	CFBundleRef mainBundle = CFBundleGetMainBundle();
	CFURLRef resourcesURL = CFBundleCopyResourcesDirectoryURL(mainBundle);
	char path[PATH_MAX];
	CFURLGetFileSystemRepresentation(resourcesURL, TRUE, (UInt8 *)path, PATH_MAX);
	std::string cppPath(path);
	std::string newFilename = cppPath + "/Assets/Sprites/" + filename;
#else
	std::string newFilename = "../Assets/Sprites/";
	newFilename += filename;
#endif


	//Load image at specified path
	SDL_Surface* loadedSurface = IMG_Load(newFilename.c_str());
	if (!loadedSurface)
	{
		printf("Unable to load image %s! SDL_image Error: %s\n", newFilename.c_str(), IMG_GetError());
	}

	return loadedSurface;
}

Texture* Bitmap::ToTexture(SDL_Surface* surface)
{
	return SDL_CreateTextureFromSurface(Game::renderer, surface);
}