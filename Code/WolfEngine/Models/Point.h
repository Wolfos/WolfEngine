#ifndef _POINT_H
#define _POINT_H
///	Point is usually used for locations in pixels
class Point{
public:
	int x;
	int y;
	/// Linear interpolation between Points
	static Point Lerp(Point from, Point to, float t);
};

/// PointF is usually used for scale
/// Same as Point, just with floating point numbers
class PointF{
public:
	float x;
	float y;
	///	Linear interpolation between PointF's
	static PointF Lerp(PointF from, PointF to, float t);
};
#endif