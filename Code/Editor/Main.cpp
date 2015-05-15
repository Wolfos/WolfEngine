#include "../WolfEngine/WolfEngine.h"
#include "Editor.h"

int main(int argc, char* args[])
{
	if (WolfEngine::Init())
	{
		Debug::Log("WolfEngine has failed to initialize.\n");
		Debug::Log("¦¦¦¦¦¦¦¦¦_¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¦¦¦¦	Wow!\n¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¯¦¦¦¦¦\n¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¯¦¦¦¦¦¦¦\n¦¦¦¦¦¦¦¦_¯¦¦¯¯¯¯___¯¦¦¦¦¦¦¦¦¦\n¦¦¦¦¦__¯¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¦¦¦¦¦¦\n¦¦¦_¯¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¯¦¦¯¦¦¦¦¦\n¦¦¦¦¦¦__¦¦¦¦¦¦¦¦¦¦¦¦¦¦¯_¦¦¦¦¦		Much error :(\n¦¦¦¦¦¦¦¯¦¦¦¦¦_¯¦_¦¦¦¦¦¦¦¦¦¦¦¦\n¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¯¦¦¦¦¦¦¦¦¯_¦¦\n¦¦¦¦_¦¦_¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦\n¯¦¯¦_¦_¦¦_¦¯¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦\n¦¦¦¦¯¦¯¦¦__¦_¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦\n¦¦¦¦¯¯__¦¦¦_¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦\n¦¦¦¦¦¦¦¦¯¯¯¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦	Many wrong\n¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¦¦¦¦¦\n¦¦¯_¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦_¦¦¦¦¦¦¦\n¦¦¦¦¯_¦¦¦¦¦¦¦¦¦¦___¯¦¦¦¦_¯¦¦¦\n¦¦¦¦¦¦¯______¯¯¯¦¦¦¦¦__¯¦¦¦¦¦\n¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¯¯¦¦¦¦¦¦¦¦\n");
		return 1;
	}

	Editor* scene = new Editor();

	WolfEngine::scene = scene;


	scene->camera->width = WolfEngine::screenWidth;
	scene->camera->height = WolfEngine::screenHeight;
	scene->camera->window = WolfEngine::window;

	scene->Start();

	WolfEngine::MainLoop();

	WolfEngine::Quit();
	return 0;
}
