#pragma once
#include "WolfMath.h"

namespace WolfMath
{
	float Sqrt(float value);
}

/// Vector of two numbers
template <class t = float> class Vector2
{
public:
	t x;
	t y;

	///	Linear interpolation
	Vector2<t> Lerp(Vector2 from, Vector2 to, float time)
	{
		Vector2 point;
		point.x = (1 - time)*from.x + time*to.x;
		point.y = (1 - time)*from.y + time*to.y;
		return point;
	}

	/// Returns the vector's magnitude
	t Magnitude()
	{
		t x2 = x * x;
		t y2 = y * y;

		return x2 + y2;
	}

	void Normalize()
	{
		float m = Magnitude();
		x /= m;
		y /= m;
	}

	Vector2& operator +(const Vector2& other)
	{
		this->x += other.x;
		this->y += other.y;
		return *this;
	}
};

/// Vector of three numbers
template <class t = float> class Vector3
{
public:
	t x;
	t y;
	t z;

	/// Linear interpolation
	static Vector3<t> Lerp(Vector3 from, Vector3 to, float time)
	{
		Vector3 point;
		point.x = (1 - time)*from.x + time*to.x;
		point.y = (1 - time)*from.y + time*to.y;
		point.z = (1 - time)*from.z + time*to.z;
		return point;
	}

	/// Returns the vector's magnitude
	t Magnitude()
	{
		t x2 = x * x;
		t y2 = y * y;
		t z2 = z * z;

		return WolfMath::Sqrt(x2 + y2 + z2);
	}

	void Normalize()
	{
		float m = Magnitude();
		x /= m;
		y /= m;
		z /= m;
	}

	static Vector3<> Cross(Vector3<> a, Vector3<> b)
	{
		Vector3<> c;
		c.x = a.y*b.z - a.z*b.y;
		c.y = a.z*b.x - a.x*b.z;
		c.z = a.x*b.y - a.y*b.x;
		return c;
	}

	Vector3 operator +(const Vector3 other)
	{
		Vector3 v;
		v.x = this->x + other.x;
		v.y = this->y + other.y;
		v.z = this->z + other.z;
		return v;
	}

	Vector3 operator +(const Vector2<t> other)
	{
		Vector3 v;
		v.x = this->x + other.x;
		v.y = this->y + other.y;
		return v;
	}

	Vector3 operator -(const Vector3 other)
	{
		Vector3 v;
		v.x = this->x - other.x;
		v.y = this->y - other.y;
		v.z = this->z - other.z;
		return v;
	}

	Vector3 operator *(const Vector3 other)
	{
		Vector3 v;
		v.x = this->x * other.x;
		v.y = this->y * other.y;
		v.z = this->z * other.z;
		return v;
	}
};