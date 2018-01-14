#ifndef _COLLISION_H
#define _COLLISION_H
#include "../Includes.h"
#include "../Models/Point.h"
namespace Collision
{
	extern bool AABB(Vector2<int> point, WRect rect);
	extern bool AABB(Vector2<float> point, WRect rect);
}
#endif