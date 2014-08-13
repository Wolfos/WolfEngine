/*
WolfEngine © 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#define _CRT_SECURE_NO_DEPRECATE //MICROSOOOOOOOOFT!
#include "Sound.h"

Sound::Sound(char* filename)
{
	#ifdef ANDROID
		char newFilename[1024] = "Audio/";
	#else
		char newFilename[1024] = "../Assets/Audio/";
	#endif
	strcat(newFilename, filename);
	sound = Mix_LoadWAV(newFilename);
	if (!sound)
	{
		printf("Unable to load audio file %s! SDL_Mixer Error: %s\n", newFilename, Mix_GetError());
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
