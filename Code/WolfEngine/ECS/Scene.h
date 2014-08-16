#ifndef _SCENE_H
#define _SCENE_H
#include <string>
#include <vector>
#include "../Rendering/Map.h"
#include "GameObject.h"

class Scene
{
	public:
		Scene();

		///	Load a scene from file
		void Load(std::string filename);

		/// Override, runs every frame
		virtual void Update()
		{
		}

		void UpdateObjects();

		void LateUpdateObjects();

		void RenderObjects();

		GameObject* camera;

	private:
		Map* map;
		std::string mapfilename;

		std::vector<GameObject*> gameObjects;
		int numObjects;
};

#endif