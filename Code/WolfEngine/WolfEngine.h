///
///	Global engine functions and values
///

#include "ECS/Scene.h"
#include <string>

namespace WolfEngine
{
    extern SDL_Window* window;

	extern Scene* scene;
	extern SDL_Renderer* renderer;
    extern SDL_GLContext context;

	extern int maxFPS;
	extern int screenWidth;
	extern int screenHeight;

	extern int Init();

	extern void MainLoop();

	extern int Quit();

	// Utility functions
	/// Get the path to the 'Assets' folder
	extern std::string FindAssetFolder();
};
