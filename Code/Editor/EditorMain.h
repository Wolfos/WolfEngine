#ifndef _EDITORMAIN_H
#define _EDITORMAIN_H

#include "../WolfEngine/WolfEngine.h"
#include "TilePicker.h"

class EditorMain
{
	public:
		void Start();
		void Update();
		void Exit();

	private:
		Map* map;
		Map* grid;
		SDL_Texture* spritesheet;
		SDL_Texture * gridtex;
		TilePicker* tilePicker;
		int tilewidth;
		int tileheight;
		bool dragging = false;
		Point initMousePos;
		Transform* camera;
		int layer = 0;
};


#endif