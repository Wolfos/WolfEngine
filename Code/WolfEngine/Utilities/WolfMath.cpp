#include "WolfMath.h"
#include <math.h>

float WolfMath::Clamp(float value, float min, float max)
{
	return value < min ? min : (value > max ? max : value);
}

int WolfMath::Floor(float value)
{
	int vi = (int)value;
	return value < vi ? vi - 1 : vi;
}

float WolfMath::Sin(float value)
{
	return sin(value);
}

float WolfMath::Cos(float value)
{
	return cos(value);
}

float WolfMath::Tan(float value)
{
	return tan(value);
}

float WolfMath::DegToRad(float degrees)
{
	return degrees * pi / 180;
}