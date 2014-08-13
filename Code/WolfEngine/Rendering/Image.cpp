/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#define _CRT_SECURE_NO_DEPRECATE //MICROSOOOOOOOOFT!
#include "Image.h"
#include "../Utilities/Debug.h"
#include "Screen.h"

#ifdef __APPLE__
#include "CoreFoundation/CoreFoundation.h"
#endif

SDL_Surface* Image::LoadSurface(std::string filename)
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

SDL_Texture* Image::ToTexture(SDL_Surface* surface)
{
	return SDL_CreateTextureFromSurface(Screen::mainCamera->screen, surface);
}


SDL_Texture* Image::Load(std::string filename)
{
	//The final texture
	SDL_Texture* newTexture = NULL;
	newTexture = ToTexture(LoadSurface(filename));

	if (!newTexture)
	{
		printf("Unable to create texture from %s! SDL Error: %s\n", filename.c_str(), SDL_GetError());
	}

	return newTexture;
}