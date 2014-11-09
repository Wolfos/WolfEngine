#ifndef _EDITOR_H
#define _EDITOR_H

#include "../WolfEngine/API.h"
#include "TilePicker.h"


class Editor : public Scene
{
	public:
		void Start();
		void Update();
		void Exit();

	private:
		Map* map;
		Map* grid;
		Bitmap* spritesheet;
		Bitmap * gridtex;
		TilePicker* tilePicker;
		int tilewidth;
		int tileheight;
		bool dragging = false;
		Point initMousePos;
		Transform* cam;
		int layer = 0;
		bool showGrid = true;
};


#endif