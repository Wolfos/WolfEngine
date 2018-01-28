//
// Created by Robin on 02/01/2018.
//
#pragma once
#include "Vectors.h"

class Quaternion
{
public:
	float x, y, z, w;

	Quaternion();

	/// Converts the Quaternion to Euler angles
	Vector3<float> ToEuler();
	/// Returns a Quaternion from Euler angles
	static Quaternion* FromEuler(Vector3<float> angles);
};
