#ifndef _CAMERA_H
#define _CAMERA_H
#include "../ECS/Component.h"
#include "../Includes.h"

///
///	A Camera component, only one of these can be present at a time and it's created before the game starts
/// Access the main camera through Screen::mainCamera
///
class Camera : public Component
{
public:
	///	The renderer object
	SDL_Renderer* screen;
	///	The width of the screen
	int width = 0;
	///	The height of the screen
	int height = 0;
	///	The window it's rendered to
	SDL_Window* window;
	virtual void Added();
	virtual void Update();
};
#endif