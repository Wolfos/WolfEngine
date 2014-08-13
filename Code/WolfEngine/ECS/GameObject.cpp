/*
WolfEngine © 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "GameObject.h"
#include "../Utilities/Debug.h"
#include "ObjectManager.h"

void GameObject::Update()
{
	for(unsigned i = 0; i!= components.bucket_count(); ++i)
	{
		for (auto local_it = components.begin(i); local_it != components.end(i); ++local_it)
		{
			local_it->second->Update();
		}
	}
}

void GameObject::LateUpdate()
{
	for (unsigned i = 0; i != components.bucket_count(); ++i)
	{
		for (auto local_it = components.begin(i); local_it != components.end(i); ++local_it)
		{
			local_it->second->LateUpdate();
		}
	}
}

GameObject::GameObject()
{
	//Every GameObject gets a transform component
	AddComponent<Transform>();
	transform = GetComponent<Transform>();
	transform->position.x = 0;
	transform->position.y = 0;
}

GameObject::~GameObject()
{
	components.clear();
	ObjectManager::DeleteObject(this);
}
