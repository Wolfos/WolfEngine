#ifndef _SCENE_H
#define _SCENE_H
#include <string>
#include <vector>
#include "../Rendering/Map.h"
#include "GameObject.h"
#include "../Components/Camera.h"

class Scene
{
	public:
		Camera* camera;
		int layers;


		Scene();
		~Scene();

		///	Load a scene from file
		/// Not actually implemented yet
		void Load(std::string filename);

		/// Override, runs every frame
		virtual void Update()
		{
		}

		void UpdateObjects();

		void LateUpdateObjects();

		void RenderObjects();

		void AddGameObject(GameObject* gameObject);

	private:
		Map* map;
		std::string mapfilename;

		std::vector<GameObject*> gameObjects;
		int numObjects = 0;
};

#endif