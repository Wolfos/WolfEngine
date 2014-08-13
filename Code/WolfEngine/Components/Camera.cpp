/*
WolfEngine © 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Camera.h"
#include "../Rendering/Screen.h"

void Camera::Added()
{
	Screen::mainCamera = this;
}

void Camera::Update()
{
	SDL_GetWindowSize(window, &width, &height);
}