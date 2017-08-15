#include "WolfMath.h"

float WolfMath::Clamp(float value, float min, float max)
{
	return value < min ? min : (value > max ? max : value);
}

int WolfMath::Floor(float value)
{
	int vi = (int)value;
	return value < vi ? vi - 1 : vi;
}