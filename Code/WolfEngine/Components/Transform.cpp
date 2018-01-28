/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Transform.h"

void Transform::Added()
{
    parent = NULL;
	localPosition = {0,0,0};
    localScale = {1,1,1};
	localRotation = new Quaternion();
}

void Transform::AddChild(Transform* child)
{
	if(children)
    {
		children->push_back(child);
    }
    
    // Remove child from previous parent
    if(child->parent)
    {
        auto it = std::find(child->parent->children->begin(), child->parent->children->end(), child);
        child->parent->children->erase(it);
    }
    child->parent = this;
}

Vector3<> Transform::GetPosition()
{
    if(parent != NULL)
    {
        return localPosition + parent->GetPosition();
    }
    else
    {
        return localPosition;
    }
}

Vector3<> Transform::GetScale()
{
	if(parent != NULL)
	{
		return localScale * parent->GetScale();
	}
	else
	{
		return localScale;
	}
}

Matrix Transform::GetMatrix()
{
	Matrix translate;
	Matrix rotate;
	Matrix scale;

	translate.SetIdentity();
	Vector3<> position = GetPosition();
	translate.Translate(position);

	rotate.FromQuat(localRotation, position);

	scale.SetIdentity();
	scale.Scale(GetScale());

	return translate * rotate * scale;
}

void Transform::Translate(Vector3<> direction)
{
	localPosition = localPosition + direction;
}
