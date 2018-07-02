/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Camera.h"
#include "../WolfEngine.h"
#include "../Math/WolfMath.h"

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
	SDL_GetWindowSize(WolfEngine::window, &width, &height);
	aspectRatio = (float)width / (float)height;
	//gameObject->transform->Translate({0, 0.01f, 0});
	//gameObject->transform->Rotate({0, 0, 1});
}

Matrix Camera::GetProjection()
{
	Matrix m;
	m.SetOrtho(-aspectRatio * ortographicSize, aspectRatio * ortographicSize, -ortographicSize, ortographicSize, clipMin, clipMax);
	//m.SetPerspective(60, aspectRatio, clipMin, clipMax);
	return m;
}

void Camera::UpdateMatrices()
{
	projection = GetProjection();
	view = gameObject->transform->GetMatrix();
	//view.ViewInverse();
	view.Invert();
}

Vector3<float> Camera::ScreenToWorldPosition(Vector2<int> screenPosition)
{
	Vector3<float> worldPos;
	worldPos.y = WolfMath::Lerp(ortographicSize, -ortographicSize, ((float)screenPosition.y) / height);
	worldPos.x = WolfMath::Lerp(-ortographicSize * aspectRatio, ortographicSize * aspectRatio, ((float)screenPosition.x) / width);

	return worldPos;
}