#pragma once
#include "../Includes.h"
#include <string>
///
/// For playing sound effects
/// Not streamed but many can be played at the same time
/// For streamed audio, see Music
///
class Sound
{
public:
	///	Loads a sound from a file located in ../Assets/Audio
	Sound(std::string filename);
	///	Plays the sound, loop 0 plays once, loop 1 plays twice, etc
	void Play(int loop = 0);
	/// Stops the sound
	void Stop();
	~Sound();
private:
	Mix_Chunk* sound;
	int channel;
};