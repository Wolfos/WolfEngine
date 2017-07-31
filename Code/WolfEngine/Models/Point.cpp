/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Point.h"

WPoint WPoint::Lerp(WPoint from, WPoint to, float t)
{
	WPoint point;
	point.x = (int)((1 - t)*from.x + t*to.x);
	point.y = (int)((1 - t)*from.y + t*to.y);
	return point;
}

WPointF WPointF::Lerp(WPointF from, WPointF to, float t)
{
	WPointF point;
	point.x = (1 - t)*from.x + t*to.x;
	point.y = (1 - t)*from.y + t*to.y;
	return point;
}