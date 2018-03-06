#ifndef _CAMERA_H
#define _CAMERA_H
#include "../ECS/Component.h"
#include "../Includes.h"
#include "../Math/Matrix.h"
#include "../Math/Vectors.h"

///
///	A Camera component, created by Scene's constructor
///
class Camera : public Component
{
public:
	virtual void Added();
	///	The width of the screen in pixels
	int width = 0;
	///	The height of the screen in pixels
	int height = 0;
	virtual void Update();

	Matrix projection;
	Matrix view;

	void UpdateMatrices();

	float ortographicSize;

	float aspectRatio;

	float clipMin, clipMax;

	Matrix GetProjection();
	Matrix GetView();
	Vector3<float> ScreenToWorldPosition(Vector2<float> screenPosition);
};
#endif