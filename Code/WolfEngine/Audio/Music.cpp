/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#define _CRT_SECURE_NO_DEPRECATE //MICROSOOOOOOOOFT!
#include "Music.h"
#include "../WolfEngine.h"

Music::Music(std::string filename)
{
	std::string path = WolfEngine::FindAssetFolder() + "Audio/" + filename;
	music = Mix_LoadMUS(path.c_str());
	if (!music)
	{
		printf("Unable to load audio file %s! SDL_Mixer Error: %s\n", path.c_str(), Mix_GetError());
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
