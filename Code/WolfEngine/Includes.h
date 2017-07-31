// Global includes for each platform

#ifndef _INCLUDES_H
	#define _INCLUDES_H
	//Includes
	#ifdef _WIN32
		#include <SDL.h>
		#include <SDL_image.h>
		#include <SDL_ttf.h>
		#include <SDL_mixer.h>
	#elif defined __ANDROID__
		#include "SDL.h"
		#include "SDL_image.h"
		#include "SDL_ttf.h"
		#include "SDL_mixer.h"
    #elif defined __APPLE__
        #include <SDL.h>
        #include <SDL_image.h>
        #include <SDL_ttf.h>
        #include <SDL_mixer.h>
	#else
		#include <SDL2/SDL.h>
		#include <SDL2/SDL_image.h>
		#include <SDL2/SDL_ttf.h>
		#include <SDL2/SDL_mixer.h>
	#endif
	
	#ifdef _SDL_H
		#define Texture SDL_Texture
		#define WRect SDL_Rect
	#endif

#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#endif
