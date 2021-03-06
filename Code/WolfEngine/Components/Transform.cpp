/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Transform.h"
#include <algorithm>

void Transform::Added()
{
    parent = NULL;
	localPosition = {0,0,0};
    localScale = {1,1,1};
	localRotation = new Quaternion();
}

void Transform::Destroy()
{
	delete localRotation;
}

void Transform::AddChild(Transform* child)
{
	children.push_back(child);

    
    // Remove child from previous parent
    if(child->parent)
    {
        auto it = std::find(child->parent->children.begin(), child->parent->children.end(), child);
        child->parent->children.erase(it);
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
	translate.Translate(localPosition);

	rotate.FromQuat(localRotation, localPosition);

	scale.SetIdentity();
	scale.Scale(localScale);

	Matrix localMatrix = scale * translate * rotate;//translate * rotate * scale;
	if(parent == NULL) return localMatrix;
	else return localMatrix * parent->GetMatrix();
}

void Transform::Translate(Vector3<> direction)
{
	localPosition = localPosition + direction;
}

void Transform::Rotate(Vector3<> eulerAngles)
{
	Quaternion* rotateBy = Quaternion::FromEuler(eulerAngles);
	localRotation->Multiply(rotateBy);
	delete rotateBy;
}