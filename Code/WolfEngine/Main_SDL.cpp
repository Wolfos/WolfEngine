/*
	WolfEngine © 2013-2014 Robin van Ee
	http://wolfengine.net
	Contact:
	rvanee@wolfengine.net
*/
#define _CRT_SECURE_NO_DEPRECATE //MICROSOOOOOOOOFT!
#include "Includes.h"
#include "../GameMain.h"
#include "Input/Input.h"
#include "Utilities/Time.h"
#include "Utilities/Debug.h"
#include "ECS/ObjectManager.h"
#include "Rendering/Screen.h"
#include "GUI/GUI.h"

//Screen dimensions
int screenWidth = 1280;
int screenHeight = 720;

SDL_Window* window = NULL;
SDL_Renderer* screenRenderer = NULL;

GameMain gameMain;

ObjectManager objMgr;

const int MAXFPS = 60; //FPS to cap at. Set to -1 to disable

int Init()
{
	//Initialize SDL
	if (SDL_Init(SDL_INIT_VIDEO) < 0)
	{
		Debug::Log("SDL could not initialize! SDL_Error: %s\n", SDL_GetError());
		return 1;
	}
	else
	{
#ifdef ANDROID 
		//Find ideal screen resolution
		//Android's screen resolution isn't actually set by CreateWindow, but this also sets the camera's width and height correctly
		SDL_DisplayMode* mode = new SDL_DisplayMode;
		SDL_GetDisplayMode(0, 0, mode);
		screenWidth = mode->w;
		screenHeight = mode->h;
#endif
		window = SDL_CreateWindow("WolfEngine", SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, screenWidth, screenHeight, SDL_WINDOW_SHOWN | SDL_WINDOW_MAXIMIZED | SDL_WINDOW_RESIZABLE);
		if (window == NULL)
		{
			Debug::Log("Window could not be created! SDL_Error: %s\n", SDL_GetError());
			return 1;
		}
		else
		{
			//Initialize SDL_Image
			int imgflags = IMG_INIT_PNG;
			if (!(IMG_Init(imgflags) & imgflags))
			{
				Debug::Log("SDL_image could not initialize! SDL_image Error: %s\n", IMG_GetError());
				return 1;
			}
			else
			{
				screenRenderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED);
				SDL_SetRenderDrawColor(screenRenderer, 0x1e, 0xAB, 0xF4, 0xFF);
			}

			//Initialize SDL_TTF
			if (TTF_Init())
			{
				Debug::Log("SDL_ttf could not initialize! SDL_ttf Error: %s\n", SDL_GetError());
				return 1;
			}

			//Initialize SDL_Mixer, returns 0 on failure
			if (!Mix_Init(MIX_INIT_OGG))
			{
				printf("SDL_Mixer could not initialize! SDL_ttf Error: %s\n", Mix_GetError());
				return 1;
			}
			else
			{
				Mix_OpenAudio(22050, AUDIO_S16, 2, 4096);
			}
		}
	}
	return 0;
}

void Pause()
{
	//Pause all sounds and music
	Mix_Pause(-1);
	Mix_PauseMusic();
}

void Resume()
{
	//Resume all sounds and music
	Mix_Resume(-1);
	Mix_ResumeMusic();
}

#ifdef ANDROID
#include <jni.h>
extern "C" 
{
	void Java_nl_rvanee_wolfengine_WolfEngine_Pause()
	{
		Pause();
	}
	void Java_nl_rvanee_wolfengine_WolfEngine_Resume()
	{
		Resume();
	}
}
#endif


///
/// WolfEngine main loop
///
void MainLoop()
{
	int quit = 0;
	SDL_Event eventHandler;
	Uint32 curFrameTime = 0;
	Uint32 lastFrameTime  = 0;
	Input input;

	while (!quit)
	{
		curFrameTime = SDL_GetTicks();
		
		Time::frameTimeS = (double)(curFrameTime - lastFrameTime) / 1000;
		int fps = (int)(1.f / Time::frameTimeS);

		//Clear the screen
		SDL_RenderClear(screenRenderer);

		input.Update(&eventHandler);

		if (eventHandler.type == SDL_WINDOWEVENT_RESIZED)
		{
			Screen::mainCamera->width = eventHandler.window.data1;
			Screen::mainCamera->height = eventHandler.window.data2;
		}

		if(eventHandler.type == SDL_QUIT)
		{
			quit = 1;
		}

		gameMain.Update();

		//Update the gameObjects
		ObjectManager::Update();

		//Render the SpriteRenderers
		ObjectManager::Render();

		GUI::Update();

		//Late update
		ObjectManager::LateUpdate();

		SDL_RenderPresent(screenRenderer);
		lastFrameTime = curFrameTime;

		if (MAXFPS!=-1 && SDL_GetTicks() - curFrameTime < 1000 / MAXFPS)
			SDL_Delay((1000 / MAXFPS) - (SDL_GetTicks() - curFrameTime));
	}
}

int main( int argc, char* args[] )
{
	if(Init())
	{
		Debug::Log("WolfEngine has failed to initialize.\n");
		Debug::Log("¦¦¦¦¦¦¦¦¦_¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¦¦¦¦	Wow!\n¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¯¦¦¦¦¦\n¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¯¦¦¦¦¦¦¦\n¦¦¦¦¦¦¦¦_¯¦¦¯¯¯¯___¯¦¦¦¦¦¦¦¦¦\n¦¦¦¦¦__¯¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¦¦¦¦¦¦\n¦¦¦_¯¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¯¦¦¯¦¦¦¦¦\n¦¦¦¦¦¦__¦¦¦¦¦¦¦¦¦¦¦¦¦¦¯_¦¦¦¦¦		Much error :(\n¦¦¦¦¦¦¦¯¦¦¦¦¦_¯¦_¦¦¦¦¦¦¦¦¦¦¦¦\n¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¯¦¦¦¦¦¦¦¦¯_¦¦\n¦¦¦¦_¦¦_¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦\n¯¦¯¦_¦_¦¦_¦¯¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦\n¦¦¦¦¯¦¯¦¦__¦_¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦\n¦¦¦¦¯¯__¦¦¦_¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦\n¦¦¦¦¦¦¦¦¯¯¯¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦	Many wrong\n¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¦¦¦¦¦\n¦¦¯_¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¦¦¦¦¦¦¦\n¦¦¦¦¯_¦¦¦¦¦¦¦¦¦¦___¯¦¦¦¦_¯¦¦¦\n¦¦¦¦¦¦¯______¯¯¯¦¦¦¦¦__¯¦¦¦¦¦\n¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¯¯¦¦¦¦¦¦¦¦\n");
		return 1;
	}

	//Initialize the camera
	GameObject* camera = ObjectManager::NewGameObject("Camera");
	camera->AddComponent<Camera>();
	camera->GetComponent<Camera>()->screen = screenRenderer;
	camera->GetComponent<Camera>()->width = screenWidth;
	camera->GetComponent<Camera>()->height = screenHeight;
	camera->GetComponent<Camera>()->window = window;

	gameMain.Start();

	MainLoop();

	gameMain.Exit();
	ObjectManager::Exit();

	SDL_DestroyRenderer(screenRenderer);
	SDL_DestroyWindow(window);

	SDL_Quit();

	return 0;
}
