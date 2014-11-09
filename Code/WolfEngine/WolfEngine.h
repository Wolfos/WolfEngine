///
///	This file includes all the engine headers
///

#ifndef _WOLFENGINE_H
#define _WOLFENGINE_H

#include "Includes.h"
#include "Audio/Sound.h"
#include "Audio/Music.h"
#include "Components/Button.h"
#include "Components/Camera.h"
#include "Components/SpriteRenderer.h"
#include "Components/Transform.h"
#include "GUI/Window.h"
#include "Includes/ECS.h"
#include "Input/Mouse.h"
#include "Input/Keyboard.h"
#include "Input/Keys.h"
#include "Models/Point.h"
#include "Rendering/Bitmap.h"
#include "Rendering/Map.h"
#include "Utilities/Debug.h"
#include "Utilities/Time.h"

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

	static ScriptMain* scripter;

private:
	static int InitSDL();
};

#endif
