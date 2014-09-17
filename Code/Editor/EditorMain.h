#ifndef _EDITORMAIN_H
#define _EDITORMAIN_H

#include "../WolfEngine/WolfEngine.h"
#include "TilePicker.h"

class EditorMain : public Scene
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