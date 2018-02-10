#ifndef _CAMERA_H
#define _CAMERA_H
#include "../ECS/Component.h"
#include "../Includes.h"
#include "../Math/Matrix.h"

///
///	A Camera component, only one of these can be present at a time and it's created before the game starts
/// Access the main camera through Screen::mainCamera
///
class Camera : public Component
{
public:
	virtual void Added();
	///	The width of the screen
	int width = 0;
	///	The height of the screen
	int height = 0;
	///	The window it's rendered to
	SDL_Window* window;
	virtual void Update();

	Matrix projection;
	Matrix view;

	void UpdateMatrices();

	float ortographicSize;

	float aspectRatio;

	float clipMin, clipMax;

	Matrix GetProjection();
};
#endif