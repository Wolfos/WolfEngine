#ifndef _EDITOR_H
#define _EDITOR_H

#include "../WolfEngine/API.h"


class Editor : public Scene
{
	public:
		void Start();
		void Update();
		void OnGUI();
		void Exit();

	private:
		Map* map;
		Map* grid;
		Bitmap* spritesheet;
		Bitmap* gridtex;
		Bitmap* topBar;
		Bitmap* selectionRect;
		int tilewidth;
		int tileheight;
		bool dragging = false;
		Point initMousePos;
		Transform* cam;
		int selected = 0;
		int layer = 0;
		Rect selRectPos;
		bool showGrid = true;
		float scrollBarPos = 0.0f;
};


#endif