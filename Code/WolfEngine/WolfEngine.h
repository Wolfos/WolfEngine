///
///	Global engine functions and values
///

#include "ECS/Scene.h"

namespace WolfEngine
{
    extern SDL_Window* window;

	extern Scene* scene;
	extern SDL_Renderer* renderer;

	extern int maxFPS;
	extern int screenWidth;
	extern int screenHeight;

	extern int Init();

	extern void MainLoop();

	extern int Quit();
};
