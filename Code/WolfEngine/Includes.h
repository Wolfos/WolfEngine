// Global includes for each platform

#ifndef _INCLUDES_H
	#define _INCLUDES_H
	//Includes
	#ifdef _WIN32
		#include <SDL.h>
		#include <SDL_ttf.h>
		#include <SDL_mixer.h>
		#include <GL/glew.h>
		#include <GL/gl.h>
	#elif defined __ANDROID__
		#include "SDL.h"
		#include "SDL_ttf.h"
		#include "SDL_mixer.h"
    #elif defined __APPLE__
        #include <SDL.h>
        #include <SDL_ttf.h>
        #include <SDL_mixer.h>
        #include <OpenGL/gl3.h>
    #elif defined __EMSCRIPTEN__
        #include <SDL.h>
        #include <SDL_ttf.h>
        #include <SDL_mixer.h>
        #include <GLES3/gl3.h>
		#include <GLES3/gl2ext.h>
    #else
		#include <SDL2/SDL.h>
		#include <SDL2/SDL_ttf.h>
		#include <SDL2/SDL_mixer.h>
        #include <GL/glew.h>
		#include <GL/gl.h>
	#endif
	
	#if defined _SDL_H || defined SDL_h_
		// TODO: Delete this once the new UI system and tilemap renderer is in place
		#define WRect SDL_Rect
	#endif

#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#endif
