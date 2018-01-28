//
// Created by Robin on 01/01/2018.
//

#include "Matrix.h"
#include <cstring>
#include "WolfMath.h"

Matrix::Matrix()
{
	SetIdentity();
}

const float* Matrix::GetData() const
{
	return data;
}

void Matrix::SetIdentity()
{
	memset(data, 0, sizeof(float) * 16);
	data[0] = 1;
	data[5] = 1;
	data[10] = 1;
	data[15] = 1;
}

void Matrix::SetPerspective(float angle, float aspect, float clipMin, float clipMax)
{
	float tangent = WolfMath::Tan(WolfMath::DegToRad(angle / 2));

	memset(data, 0, sizeof(float) * 16);
	data[0] = 0.5f / tangent;
	data[5] = 0.5f * aspect / tangent;
	//data[10] = -(clipMax + clipMin) / (clipMax - clipMin);
	data[11] = -1;
	data[14] = (-2 * clipMax * clipMin) / (clipMax - clipMin);
}

void Matrix::SetOrtho(float left, float right, float top, float bottom, float clipMin, float clipMax)
{
	memset(data, 0, sizeof(float) * 16);
	data[0] = 2 / (right - left);
	data[5] = 2 / (top - bottom);
	data[10] = -2 / (clipMax - clipMin);
	data[15] = 1;
}

void Matrix::Translate(Vector3<float> direction)
{
	data[12] += direction.x;
	data[13] += direction.y;
	data[14] += direction.z;
}

void Matrix::Scale(Vector3<float> scale)
{
	data[0] = scale.x;
	data[5] = scale.y;
	data[10] = scale.z;
}


Matrix Matrix::operator * (const Matrix& m) const {
	Matrix ret;

	ret.data[0] = ((data[0]*m.data[0])+(data[1]*m.data[4])+(data[2]*m.data[8])+(data[3]*m.data[12]));
	ret.data[1] = ((data[0]*m.data[1])+(data[1]*m.data[5])+(data[2]*m.data[9])+(data[3]*m.data[13]));
	ret.data[2] = ((data[0]*m.data[2])+(data[1]*m.data[6])+(data[2]*m.data[10])+(data[3]*m.data[14]));
	ret.data[3] = ((data[0]*m.data[3])+(data[1]*m.data[7])+(data[2]*m.data[11])+(data[3]*m.data[15]));

	ret.data[4] = ((data[4]*m.data[0])+(data[5]*m.data[4])+(data[6]*m.data[8])+(data[7]*m.data[12]));
	ret.data[5] = ((data[4]*m.data[1])+(data[5]*m.data[5])+(data[6]*m.data[9])+(data[7]*m.data[13]));
	ret.data[6] = ((data[4]*m.data[2])+(data[5]*m.data[6])+(data[6]*m.data[10])+(data[7]*m.data[14]));
	ret.data[7] = ((data[4]*m.data[3])+(data[5]*m.data[7])+(data[6]*m.data[11])+(data[7]*m.data[15]));

	ret.data[8] = ((data[8]*m.data[0])+(data[9]*m.data[4])+(data[10]*m.data[8])+(data[11]*m.data[12]));
	ret.data[9] = ((data[8]*m.data[1])+(data[9]*m.data[5])+(data[10]*m.data[9])+(data[11]*m.data[13]));
	ret.data[10] = ((data[8]*m.data[2])+(data[9]*m.data[6])+(data[10]*m.data[10])+(data[11]*m.data[14]));
	ret.data[11] = ((data[8]*m.data[3])+(data[9]*m.data[7])+(data[10]*m.data[11])+(data[11]*m.data[15]));

	ret.data[12] = ((data[12]*m.data[0])+(data[13]*m.data[4])+(data[14]*m.data[8])+(data[15]*m.data[12]));
	ret.data[13] = ((data[12]*m.data[1])+(data[13]*m.data[5])+(data[14]*m.data[9])+(data[15]*m.data[13]));
	ret.data[14] = ((data[12]*m.data[2])+(data[13]*m.data[6])+(data[14]*m.data[10])+(data[15]*m.data[14]));
	ret.data[15] = ((data[12]*m.data[3])+(data[13]*m.data[7])+(data[14]*m.data[11])+(data[15]*m.data[15]));

	return ret;
}

void Matrix::FromQuat(Quaternion *q, Vector3<float> pivot)
{
	SetIdentity();

	float sqw = q->w*q->w;
	float sqx = q->x*q->x;
	float sqy = q->y*q->y;
	float sqz = q->z*q->z;
	data[0] = sqx - sqy - sqz + sqw; // since sqw + sqx + sqy + sqz =1
	data[5] = -sqx + sqy - sqz + sqw;
	data[10] = -sqx - sqy + sqz + sqw;

	double tmp1 = q->x*q->y;
	double tmp2 = q->z*q->w;
	data[4] = 2.0 * (tmp1 + tmp2);
	data[1] = 2.0 * (tmp1 - tmp2);

	tmp1 = q->x*q->z;
	tmp2 = q->y*q->w;
	data[8] = 2.0 * (tmp1 - tmp2);
	data[2] = 2.0 * (tmp1 + tmp2);

	tmp1 = q->y*q->z;
	tmp2 = q->x*q->w;
	data[9] = 2.0 * (tmp1 + tmp2);
	data[6] = 2.0 * (tmp1 - tmp2);

	float a1 = pivot.x;
	float a2 = pivot.y;
	float a3 = pivot.z;

	data[12] = a1 - a1 * data[0] - a2 * data[4] - a3 * data[8];
	data[13] = a2 - a1 * data[1] - a2 * data[5] - a3 * data[9];
	data[14] = a3 - a1 * data[2] - a2 * data[6] - a3 * data[10];
	data[3] = data[7] = data[11] = 0.0;
	data[15] = 1.0;
}