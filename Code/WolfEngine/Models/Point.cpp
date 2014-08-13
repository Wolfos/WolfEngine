/*
WolfEngine © 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Point.h"

Point Point::Lerp(Point from, Point to, float t)
{
	Point point;
	point.x = (int)((1 - t)*from.x + t*to.x);
	point.y = (int)((1 - t)*from.y + t*to.y);
	return point;
}

PointF PointF::Lerp(PointF from, PointF to, float t)
{
	PointF point;
	point.x = (1 - t)*from.x + t*to.x;
	point.y = (1 - t)*from.y + t*to.y;
	return point;
}