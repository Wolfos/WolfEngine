#pragma once
#include "../ECS/Component.h"
#include "../Math/Vectors.h"
#include "../Math/Quaternion.h"
#include "../Math/Matrix.h"
#include <vector>
#include <string>
///
/// Transform component, handles position, scale and rotation
///	Added to each GameObject by default, can be accessed through gameObject->transform
///
class Transform : public Component
{
public:
	///	Local position in world units
	Vector3<> localPosition;
	/// Local scale
	Vector3<> localScale;
	///	Local rotation
	Quaternion* localRotation;
    
	virtual void Added();
	virtual void Destroy();
    /// Returns the global position of the transform
    Vector3<> GetPosition();
	/// Returns the global scale of the transform
	Vector3<> GetScale();
	/// Converts the transform to a transformation matrix
	Matrix GetMatrix();
    /// Add a child to the transform
    void AddChild(Transform* child);
    /// Returns the first child with this name
    Transform* GetChild(std::string name);
    /// Returns a pointer to the list of children
    std::vector<Transform*> GetChildren();
    /// Move in direction
    void Translate(Vector3<> direction);
	/// Rotate by Euler angles
	void Rotate(Vector3<> eulerAngles);
protected:
    Transform* parent;
    std::vector<Transform*> children;
};