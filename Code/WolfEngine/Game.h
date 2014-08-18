#ifndef _GAME_H
#define _GAME_H

#include "ECS/Scene.h"
#include "Includes.h"

class Game
{
	public:
		static Scene* scene;
		static SDL_Renderer* renderer;
};

#endif