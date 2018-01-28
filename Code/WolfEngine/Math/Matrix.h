//
// Created by Robin on 01/01/2018.
//
#pragma once
#include "Vectors.h"
#include "Quaternion.h"

class Matrix
{
private:
	float data[16];
public:
	Matrix();
	~Matrix() = default;

	const float* GetData() const;

	void SetIdentity();

	/// Sets a perspective projection matrix
	void SetPerspective(float angle, float aspect, float clipMin, float clipMax);

	/// Sets an ortographic projection matrix
	void SetOrtho(float left, float right, float top, float bottom, float clipMin, float clipMax);

	/// Move in direction
	void Translate(Vector3<float> direction);

	void Scale(Vector3<float> scale);

	Matrix operator * (const Matrix& m) const;

	/// Generates a rotation Matrix from a Quaternion
	void FromQuat(Quaternion* q, Vector3<float> pivot);
};

