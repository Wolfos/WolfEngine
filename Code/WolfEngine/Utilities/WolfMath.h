#ifndef _WOLFMATH_H
#define _WOLFMATH_H
namespace WolfMath
{
	const float pi = 3.14159265f;
	float Clamp(float value, float min, float max);
	int Floor(float value);
	float Sin(float value);
	float Cos(float value);
	float Tan(float value);
	float DegToRad(float degrees);
}
#endif