//
// Created by Robin on 01/01/2018.
//

#include "Matrix.h"
#include <cstring>
#include "../Utilities/WolfMath.h"

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
	data[10] = -(clipMin * 2) / (clipMax - clipMin);
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

void Matrix::Translate(float x, float y, float z)
{
	data[12] += x;
	data[13] += y;
	data[14] += z;
}

void Matrix::Scale(float x, float y, float z)
{
	data[0] = x;
	data[5] = y;
	data[10] = z;
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