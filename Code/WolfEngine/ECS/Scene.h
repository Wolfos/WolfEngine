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
		virtual void Update(){}

		/// Override, runs every frame, after everything else
		virtual void OnGUI(){}

		void UpdateObjects();

		void LateUpdateObjects();

		void RenderObjects();

		void AddGameObject(GameObject* gameObject);

	private:
		std::vector<GameObject*> gameObjects;
		int numObjects = 0;
};

#endif