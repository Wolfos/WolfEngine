#ifndef _MAP_H
#define _MAP_H

#include "../Includes.h"
#include "../ECS/Component.h"
#include "../ECS/GameObject.h"
#include "Bitmap.h"

///
/// A WolfEngine tilemap object
///
class Map {
	public:
		int width; /// Map width in tiles
		int height; /// Map height in tiles
		int layers; /// Number of layers in the map
		int *data; /// Pointer to the map's tiledata array
		float scale = 1;

		///	Create a new map filled with the default value, doesn't need to be called if you're going to be loading it from file
		Map(int width, int height, int layers, int defaultValue, float scale = 1);

		///	Load a map from file
        void Load(std::string filename);

		///	Render the map to an SDL_Renderer
		void Render(int layer, Bitmap* spritesheet,
			int tilewidth, int tileheight, int offset, GameObject* camera);

		///	Write the map to file
        void Write(std::string filename);

		///	Get the tile value at X, Y, L
		int Get(int x, int y, int l);

		///	Put a value in the map at X, Y, L
		void Put(int x, int y, int l, int value);
};


#endif
