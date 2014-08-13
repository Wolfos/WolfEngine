/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "ObjectManager.h"
#include "../Utilities/Debug.h"
#include "../Components/SpriteRenderer.h"
#include "../Rendering/Screen.h"

std::vector<GameObject*> ObjectManager::gameObjects;
int ObjectManager::numObjects = 0;

void ObjectManager::Update()
{
	for (unsigned int i = 0; i<gameObjects.size(); i++)
	{
		static_cast<GameObject*>(gameObjects[i])->Update();
	}
}

void ObjectManager::Render()
{
	for (unsigned int i = 0; i < gameObjects.size(); i++)
	{
		if (gameObjects[i]->GetComponent<SpriteRenderer>()!=NULL)
		{
			for (int j = 0; j < Screen::layers; j++)
			{
				if (gameObjects[i]->GetComponent<SpriteRenderer>()->layer == j)
				{
					gameObjects[i]->GetComponent<SpriteRenderer>()->Render();
				}
			}
		}
	}
}

void ObjectManager::LateUpdate()
{
	for (unsigned int i = 0; i<gameObjects.size(); i++)
	{
		static_cast<GameObject*>(gameObjects[i])->LateUpdate();
	}
}


void ObjectManager::Exit()
{
	gameObjects.clear();
}


GameObject* ObjectManager::NewGameObject(char* name)
{
	GameObject* newObject = new GameObject;

	newObject->name = name;
	newObject->id = numObjects;
	numObjects++;

	gameObjects.push_back(newObject);

	return newObject;
}


void ObjectManager::DeleteObject(GameObject* object)
{
	for (int i = 0; i < gameObjects.size(); i++)
	{
		if (gameObjects[i]->id == object->id)
		{
			gameObjects.erase(gameObjects.begin() + i);
			break;
		}
	}
}