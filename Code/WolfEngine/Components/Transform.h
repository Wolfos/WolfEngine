#ifndef _TRANSFORM_H
#define _TRANSFORM_H
#include "../ECS/Component.h"
#include "../Models/Point.h"
#include <vector>
#include <string>
///
/// Transform component, handles position, scale and rotation
///	Added to each GameObject by default, can be accessed through gameObject->transform
///
class Transform : public Component
{
public:
	///	The position in pixels
	WPoint localPosition;
    WPoint relativeTo;
	/// The scale, not in pixels
	WPointF scale;
	///	Rotation by degrees
	double angle = 0;
	///	If we want to ignore the camera position
	bool ignoreCam = false;
    
	virtual void Added();
    /// Returns the global position of the transform
    WPoint GetPosition();
    /// Add a child to the transform
    void AddChild(Transform* child);
    /// Returns the first child with this name
    Transform* GetChild(std::string name);
    /// Returns a pointer to the list of children
    std::vector<Transform*>* GetChildren();
    /// Move by pixels
    void Move(int x, int y);
protected:
    Transform* parent;
    std::vector<Transform*>* children;
};
#endif
