//
// Created by Robin on 02/01/2018.
//

#include "Quaternion.h"
#include "WolfMath.h"

using namespace WolfMath;

Quaternion::Quaternion()
{
	x = 0;
	y = 0;
	z = 0;
	w = 1;
}

Vector3<float> Quaternion::ToEuler()
{
	Vector3<float> eulerAngles;
	float test = x*y + z*w;
	if (test > 0.499) { // singularity at north pole
		eulerAngles.x = 2 * Atan2(x,w);
		eulerAngles.y = pi/2;
		eulerAngles.z = 0;
		return eulerAngles;
	}
	if (test < -0.499) { // singularity at south pole
		eulerAngles.x = -2 * Atan2(x,w);
		eulerAngles.y = - pi/2;
		eulerAngles.z = 0;
		return eulerAngles;
	}
	float sqx = x*x;
	float sqy = y*y;
	float sqz = z*z;
	eulerAngles.x = Atan2(2*y*w-2*x*z , 1 - 2*sqy - 2*sqz);
	eulerAngles.y = Asin(2*test);
	eulerAngles.z = Atan2(2*x*w-2*y*z , 1 - 2*sqx - 2*sqz);
	return eulerAngles;
}

Quaternion* Quaternion::FromEuler(Vector3<float> eulerAngles)
{
	float c1 = Cos(eulerAngles.x);
	float c2 = Cos(eulerAngles.y);
	float c3 = Cos(eulerAngles.z);
	float s1 = Sin(eulerAngles.x);
	float s2 = Sin(eulerAngles.y);
	float s3 = Sin(eulerAngles.z);

	Quaternion* q = new Quaternion();
	q->w = Sqrt(1 + c1 * c2 + c1 * c3 - s1 * s2 * s3 + c2 * c3) / 2;
	q->x = (c2 * s3 + c1 * s3 + s1 * s2 * c3) / (4 * q->w);
	q->y = (s1 * c2 + s1 * c3 + c1 * s2 * s3) / (4 * q->w);
	q->z = (-s1 * s3 + c1 * s2 * c3 + s2) /(4 * q->w);

	return q;
}