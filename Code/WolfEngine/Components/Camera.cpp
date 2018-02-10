/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Camera.h"
#include "../WolfEngine.h"

void Camera::Added()
{
	ortographicSize = 1;
	width = WolfEngine::screenWidth;
	height = WolfEngine::screenHeight;
	aspectRatio = (float)width / (float)height;
	clipMin = 0.01f;
	clipMax = 100.0f;
}

void Camera::Update()
{
	SDL_GetWindowSize(window, &width, &height);
	aspectRatio = (float)width / (float)height;
}

Matrix Camera::GetProjection()
{
	Matrix m;
	m.SetOrtho(-aspectRatio * ortographicSize, aspectRatio * ortographicSize, -1 * ortographicSize, 1 * ortographicSize, clipMin, clipMax);
	return m;
}

void Camera::UpdateMatrices()
{
	projection = GetProjection();
	view = gameObject->transform->GetMatrix();
	view.ViewInverse();
}