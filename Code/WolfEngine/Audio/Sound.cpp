/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#define _CRT_SECURE_NO_DEPRECATE //MICROSOOOOOOOOFT!
#include "Sound.h"
#include "../WolfEngine.h"

Sound::Sound(std::string filename)
{
	std::string path = WolfEngine::FindAssetFolder() + "Audio/" + filename;

	sound = Mix_LoadWAV(path.c_str());
	if (!sound)
	{
		printf("Unable to load audio file %s! SDL_Mixer Error: %s\n", path.c_str(), Mix_GetError());
	}
}

void Sound::Play(int loop)
{
	channel = Mix_PlayChannel(-1, sound, loop);
}

void Sound::Stop()
{
	Mix_HaltChannel(channel);
}
Sound::~Sound()
{
	Mix_HaltChannel(channel);
	Mix_FreeChunk(sound);
}
