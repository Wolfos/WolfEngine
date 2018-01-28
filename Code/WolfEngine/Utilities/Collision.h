#pragma once
#include "../Includes.h"
#include "../Math/Vectors.h"
namespace Collision
{
	extern bool AABB(Vector2<int> point, WRect rect);
	extern bool AABB(Vector2<float> point, WRect rect);
}