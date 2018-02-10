//
// Created by Robin on 02/01/2018.
//

#include "Quaternion.h"
#include "WolfMath.h"

using namespace WolfMath;

Quaternion::Quaternion(float x, float y, float z, float w)
{
	this->x = x;
	this->y = y;
	this->z = z;
	this->w = w;
}

Vector3<float> Quaternion::ToEuler()
{
	Vector3<float> e;
	float sqw = w*w;
	float sqx = x*x;
	float sqy = y*y;
	float sqz = z*z;
	float unit = sqx + sqy + sqz + sqw; // if normalised is one, otherwise is correction factor
	float test = x*y + z*w;
	if (test > 0.499*unit) { // singularity at north pole
		e.y = RadToDeg(2 * Atan2(x,w));
		e.z = RadToDeg(pi/2);
		e.x = 0;
		return e;
	}
	if (test < -0.499*unit) { // singularity at south pole
		e.y = RadToDeg(-2 * Atan2(x,w));
		e.z = RadToDeg(pi/2);
		e.x = 0;
		return e;
	}
	e.y = RadToDeg(Atan2(2*y*w-2*x*z , sqx - sqy - sqz + sqw));
	e.z = RadToDeg(Asin(2*test/unit));
	e.x = RadToDeg(Atan2(2*x*w-2*y*z , -sqx + sqy - sqz + sqw));
	return e;
}

Vector3<float> Quaternion::Forward()
{
	return {
			2 * (x * z + w * y),
			2 * (y*z - w*x),
			1 - 2 * (x*x + y*y)
	};
}

Vector3<float> Quaternion::Up()
{
	return {
			2 * (x*y - w*z),
			1 - 2 * (x*x + z*z),
			2 * (y*z + w*x)
	};
}

Vector3<float> Quaternion::Right()
{
	return {
			-(1 - 2 * (y*y + z*z)),
			-(2 * (x*y + w*z)),
			-(2 * (x*z - w*y))
	};
}

Quaternion* Quaternion::FromEuler(Vector3<float> eulerAngles)
{
	eulerAngles.x = DegToRad(eulerAngles.x);
	eulerAngles.y = DegToRad(eulerAngles.y);
	eulerAngles.z = DegToRad(eulerAngles.z);

	float cy = Cos(eulerAngles.z * 0.5f);
	float sy = Sin(eulerAngles.z * 0.5f);
	float cr = Cos(eulerAngles.y * 0.5f);
	float sr = Sin(eulerAngles.y * 0.5f);
	float cp = Cos(eulerAngles.x * 0.5f);
	float sp = Sin(eulerAngles.x * 0.5f);

	Quaternion* q = new Quaternion();
	q->w = cy * cr * cp + sy * sr * sp;
	q->x = cy * sr * cp - sy * cr * sp;
	q->y = cy * cr * sp + sy * sr * cp;
	q->z = sy * cr * cp - cy * sr * sp;
	return q;
}

void Quaternion::Multiply(Quaternion *other)
{
	x = x * other->w + y * other->z - z * other->y + w * other->x;
	y = -x * other->z + y * other->w + z * other->x + w * other->y;
	z =  x * other->y - y * other->x + z * other->w + w * other->z;
	w = -x * other->x - y * other->y - z * other->z + w * other->w;
}