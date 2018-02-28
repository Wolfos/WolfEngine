#include "Scene.h"
#include "../Components/SpriteRenderer.h"

Scene::Scene()
{
	GameObject *cam = new GameObject();
	camera = cam->AddComponent<Camera>();
	AddGameObject(cam);
}

Scene::~Scene()
{
	for(int i = 0; i < gameObjects.size(); i++)
	{
		delete gameObjects[i];
	}
	gameObjects.clear();
}

void Scene::Load(std::string filename)
{

}

void Scene::UpdateObjects()
{
	for (unsigned int i = 0; i<gameObjects.size(); i++)
	{
		static_cast<GameObject*>(gameObjects[i])->Update();
	}
}

void Scene::LateUpdateObjects()
{
	for (unsigned int i = 0; i<gameObjects.size(); i++)
	{
		static_cast<GameObject*>(gameObjects[i])->LateUpdate();
	}
}

void Scene::RenderObjects()
{
	for (unsigned int i = 0; i < gameObjects.size(); i++)
	{
		if (gameObjects[i]->GetComponent<SpriteRenderer>() != NULL)
		{
			for (int j = 0; j < layers; j++)
			{
				if (gameObjects[i]->GetComponent<SpriteRenderer>()->layer == j)
				{
					gameObjects[i]->GetComponent<SpriteRenderer>()->Render(camera);
				}
			}
		}
	}
}

void Scene::AddGameObject(GameObject* gameObject)
{
	gameObjects.push_back(gameObject);
	gameObject->id = numObjects;
	numObjects++;
}