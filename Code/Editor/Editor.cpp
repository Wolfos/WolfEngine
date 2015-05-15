#include "Editor.h"


void Editor::Start()
{
	spritesheet = new Bitmap("Terrain.png");
	gridtex = new Bitmap("Grid.png");
	selectionRect = new Bitmap("SelectionRect.png");

	map = new Map(100, 100, 3, -1);
	grid = new Map(100, 100, 1, 0);

	tilewidth = 128;
	tileheight = 128;

	selRectPos.x = -100;


	cam = camera->gameObject->transform;
}


void Editor::Update()
{
	int xMPos = (Mouse::position.x + cam->position.x) / tilewidth;
	int yMPos = (Mouse::position.y + cam->position.y) / tileheight;

	map->Render(0, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
	map->Render(1, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
	map->Render(2, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
		
	if (showGrid) grid->Render(0, gridtex, tilewidth, tileheight, 0, camera->gameObject);

	//Controls
	if (Mouse::position.x + cam->position.x >= 0 && Mouse::position.x + cam->position.x <= map->width * tilewidth && Mouse::position.y + cam->position.y >= 0 && Mouse::position.y + cam->position.y <= map->height * tileheight)
	{
		if (!Mouse::overGUI)
		{
			if(Mouse::KeyDown(0)) map->Put(xMPos, yMPos, layer, selected);
			if(Mouse::KeyDown(1)) map->Put(xMPos, yMPos, layer, -1);
		}
	}

	if (Keyboard::KeyClicked(Keys::G)) showGrid = !showGrid;

	if (Keyboard::KeyClicked(Keys::A)) layer = 0;
	if (Keyboard::KeyClicked(Keys::B)) layer = 1;
	if (Keyboard::KeyClicked(Keys::C)) layer = 2;

	if (Keyboard::KeyDown(Keys::W)) cam->Move(0, -10);
	if (Keyboard::KeyDown(Keys::A)) cam->Move(-10, 0);
	if (Keyboard::KeyDown(Keys::S)) cam->Move(0, 10);
	if (Keyboard::KeyDown(Keys::D)) cam->Move(10, 0);

	if (Keyboard::KeyDown(Keys::P)) map->Write("Map.wolfmap");
	if (Keyboard::KeyDown(Keys::L)) map->Load("Map.wolfmap");
}

void Editor::OnGUI()
{
	Mouse::overGUI = false;
	// Draw tile selector
	Rect rect = { camera->width - 256 - 22, 0, 256, 256 };
	GUI::Box(rect);
	Rect srcRect = { 0, scrollBarPos, 1024, 1024 };
	Rect dstRect = {rect.x + 2, rect.y + 2, 252, 252};
	spritesheet->Blit(&srcRect, &dstRect);

	int realTileWidth = tilewidth / (srcRect.w / dstRect.w);
	int realTileHeight = tileheight / (srcRect.h / dstRect.h);

	if (GUI::Button({ rect.x, 256, 64, 64 }, "A"))
	{
		layer = 0;
	}
	if (GUI::Button({ rect.x + 64, 256, 64, 64 }, "B"))
	{
		layer = 1;
	}
	if (GUI::Button({ rect.x + 128, 256, 64, 64 }, "C"))
	{
		layer = 2;
	}

	if(selRectPos.x == -100) selRectPos = { dstRect.x, dstRect.y, realTileWidth, realTileHeight };

	//Select tile
	if (Mouse::KeyReleased(0))
	{
		if (Collision::AABB(Mouse::position, dstRect))
		{
			int xPos = (Mouse::position.x - dstRect.x + srcRect.x) / realTileWidth;
			int yPos = (Mouse::position.y - dstRect.y + srcRect.y) / realTileHeight;
			selected = xPos + yPos * (spritesheet->size.x / tilewidth);
			
			selRectPos.x = xPos * realTileWidth + dstRect.x - srcRect.x;
			selRectPos.y = yPos * realTileHeight + dstRect.y - srcRect.y;
			selRectPos.w = realTileWidth;
			selRectPos.h = realTileHeight;
		}
	}
	selectionRect->Blit(selectionRect->rect, &selRectPos);

	scrollBarPos = GUI::VerticalScrollBar({ rect.x + rect.w, rect.y, 22, rect.h }, scrollBarPos, 0, spritesheet->size.y - srcRect.h);
}

void Editor::Exit()
{
	delete spritesheet;
	delete gridtex;
	delete map;
	delete grid;
}