#ifndef _MUSIC_H
#define _MUSIC_H
#include "../Includes.h"
#include <string>
///
/// For playing music
/// Music is streamed but only one can be played at the same time
/// For sounds that can be played at the same time, see Sound
///
class Music
{
public:
	///	Loads a song from a file located in ../Assets/Audio
	Music(std::string filename);
	///	Plays the music, loop 0 plays once, loop 1 plays twice, etc
	void Play(int loop = 0);
	///	Stops the music
	void Stop();
	~Music();
private:
	Mix_Music* music;
};
#endif