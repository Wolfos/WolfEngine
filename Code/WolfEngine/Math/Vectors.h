#pragma once

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

	Vector3& operator +(const Vector3& other)
	{
		this->x += other.x;
		this->y += other.y;
		this->z += other.z;
		return *this;
	}

	Vector3& operator +(const Vector2<t>& other)
	{
		this->x += other.x;
		this->y += other.y;
		return *this;
	}

	Vector3& operator -(const Vector3& other)
	{
		this->x -= other.x;
		this->y -= other.y;
		this->z -= other.z;
		return *this;
	}

	Vector3& operator *(const Vector3& other)
	{
		this->x *= other.x;
		this->y *= other.y;
		this->z *- other.z;
		return *this;
	}
};