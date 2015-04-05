///
///	This file includes all the engine headers
///

#ifndef _WOLFENGINE_H
#define _WOLFENGINE_H

#include "ECS/Scene.h"

class WolfEngine
{
public:
	static SDL_Window* window;

	static Scene* scene;
	static SDL_Renderer* renderer;

	static int maxFPS;
	static int screenWidth;
	static int screenHeight;

	static int Init();

	static void MainLoop();

	static int Quit();

private:
	static int InitSDL();
};

#endif
