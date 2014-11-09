/*
WolfEngine © 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "GameObject.h"
#include "../Utilities/Debug.h"
#include "../Components/Script.h"

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

Script* GameObject::AddComponent(std::string filename)
{
	Script* s = new Script(filename);
	components[&typeid(*s)] = s;
	s->gameObject = this;
	s->Added();

	return s;
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
	// TODO: implement
	//Game::scene::DeleteObject(this);
}
