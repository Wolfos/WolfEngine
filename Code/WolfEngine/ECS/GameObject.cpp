/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "GameObject.h"
#include "../Utilities/Debug.h"

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
}

GameObject::~GameObject()
{
	for (unsigned i = 0; i != components.bucket_count(); ++i)
	{
		for (auto local_it = components.begin(i); local_it != components.end(i); ++local_it)
		{
			local_it->second->Destroy();
			delete local_it->second;
		}
	}
}
