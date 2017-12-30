#ifndef _POINT_H
#define _POINT_H
///	WPoint is usually used for locations in pixels
class WPoint{
public:
    WPoint operator+=(const WPoint& b)
    {
        this->x += b.x;
        this->y += b.y;
        return *this;
    }
    
    WPoint operator+(const WPoint& b) {
        WPoint point;
        point.x = this->x + b.x;
        point.y = this->y + b.y;
        return point;
    }
    
    WPoint operator-(const WPoint& b)
    {
        WPoint point;
        point.x = this->x - b.x;
        point.y = this->y - b.y;
        return point;
    }
    
	int x;
	int y;
	/// Linear interpolation between Points
	static WPoint Lerp(WPoint from, WPoint to, float t);
};

/// WPointF is usually used for scale
/// Same as WPoint, just with floating point numbers
class WPointF{
public:
	float x;
	float y;
	///	Linear interpolation between PointF's
	static WPointF Lerp(WPointF from, WPointF to, float t);
};
#endif
