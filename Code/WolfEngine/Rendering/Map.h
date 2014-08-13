#ifndef _MAP_H
#define _MAP_H

#include "../Includes.h"
#include "../Includes/ECS.h"

///
/// A WolfEngine tilemap object
///
class Map {
	public:
		int width; /// Map width in tiles
		int height; /// Map height in tiles
		int layers; /// Number of layers in the map
		int *data; /// Pointer to the map's tiledata array
		//int *events; /// Pointer to the map's event data array

		///	Create a new map filled with the default value, doesn't need to be called if you're going to be loading it from file
		Map(int width, int height, int layers, int defaultValue);

		///	Load a map from file
		void Load(char* filename);

		///	Render the map to an SDL_Renderer
		void Render(SDL_Renderer* target, int layer, SDL_Texture* spritesheet,
			int tilewidth, int tileheight, int offset, GameObject* camera);

		///	Write the map to file
		void Write(char* filename);

		///	Get the tile value at X, Y, L
		int Get(int x, int y, int l);

		///	Put a value in the map at X, Y, L
		void Put(int x, int y, int l, int value);
};


#endif