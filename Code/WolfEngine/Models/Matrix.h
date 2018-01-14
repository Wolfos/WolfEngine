//
// Created by Robin on 01/01/2018.
//
#pragma once

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

	void Translate(float x, float y, float z);

	void Scale(float x, float y, float z);

	Matrix operator * (const Matrix& m) const;
};

