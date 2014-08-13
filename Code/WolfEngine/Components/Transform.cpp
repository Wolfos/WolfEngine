/*
WolfEngine © 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Transform.h"
#include <stdio.h>
#include "../Utilities/Debug.h"

void Transform::Added()
{
	scale.x = 1;
	scale.y = 1;
}

void Transform::Move(int x, int y)
{
	position.x += x;
	position.y += y;
}