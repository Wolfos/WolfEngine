/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Transform.h"
#include <stdio.h>
#include "../Utilities/Debug.h"

void Transform::Added()
{
    parent = NULL;
	scale.x = 1;
	scale.y = 1;
    localPosition.x = 0;
    localPosition.y = 0;
    relativeTo.x = 0;
    relativeTo.y = 0;
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

WPoint Transform::GetPosition()
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

void Transform::Move(int x, int y)
{
	localPosition.x += x;
	localPosition.y += y;
}
