/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#define _CRT_SECURE_NO_DEPRECATE //MICROSOOOOOOOOFT!
#include "Music.h"

#ifdef __APPLE__
#include "CoreFoundation/CoreFoundation.h"
#endif

Music::Music(std::string filename)
{
#ifdef ANDROID
	std::string newFilename = "Audio/";
#elif defined __APPLE__
    //Find the resources folder
    CFBundleRef mainBundle = CFBundleGetMainBundle();
    CFURLRef resourcesURL = CFBundleCopyResourcesDirectoryURL(mainBundle);
    char path[PATH_MAX];
    CFURLGetFileSystemRepresentation(resourcesURL, TRUE, (UInt8 *)path, PATH_MAX);
    std::string cppPath(path);
    std::string newFilename = cppPath + "/Assets/Audio/" + filename;
#else
    std::string newFilename = "../Assets/Audio/";
#endif
    newFilename += filename;
	music = Mix_LoadMUS(newFilename.c_str());
	if (!music)
	{
		printf("Unable to load audio file %s! SDL_Mixer Error: %s\n", newFilename.c_str(), Mix_GetError());
	}
}

void Music::Play(int loop)
{
	Mix_PlayMusic(music, loop);
}

void Music::Stop()
{
	Mix_HaltMusic();
}
Music::~Music()
{
	Mix_HaltMusic();
	Mix_FreeMusic(music);
}
