#include "WolfMath.h"

float WolfMath::Clamp(float value, float min, float max)
{
	return value < min ? min : (value > max ? max : value);
}