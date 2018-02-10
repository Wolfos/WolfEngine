#pragma once
#include "Vectors.h"
template <class t> class Vector3;

namespace WolfMath
{
	const float pi = 3.14159265359f;
	float Clamp(float value, float min, float max);
	int Floor(float value);
	float Sin(float value);
	float Cos(float value);
	float Tan(float value);
	float DegToRad(float degrees);
	float RadToDeg(float radians);
	float Sqrt(float value);
	float Atan2(float y, float x);
	float Asin(float value);
	float Dot(Vector3<float> a, Vector3<float>b);
}