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
	// Render our map
	map->Render(0, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
	map->Render(1, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
	map->Render(2, spritesheet, tilewidth, tileheight, 0, camera->gameObject);

	// Render the grid
	if (showGrid) grid->Render(0, gridtex, tilewidth, tileheight, 0, camera->gameObject);
	
	if (Mouse::overGUI) canDraw = false;
	else if (!Mouse::KeyDown(0)) canDraw = true;
	// Determine if we clicked on the map
	if 
	(	Mouse::position.x + cam->position.x >= 0 && 
		Mouse::position.x + cam->position.x <= map->width * tilewidth && 
		Mouse::position.y + cam->position.y >= 0 && 
		Mouse::position.y + cam->position.y <= map->height * tileheight)
	{
		if (canDraw)
		{
			// Determine the coordinates of the tile the cursor is over
			int xMPos = (Mouse::position.x + cam->position.x) / tilewidth;
			int yMPos = (Mouse::position.y + cam->position.y) / tileheight;

			// LMB places a tile, RMB removes one
			if(Mouse::KeyDown(0)) map->Put(xMPos, yMPos, layer, selected);
			if(Mouse::KeyDown(1)) map->Put(xMPos, yMPos, layer, -1);
		}
	}

	// Keyboard shortcuts
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
	WRect rect = { camera->width - 256 - 22, 0, 256, 256 };
	GUI::Box(rect);
	WRect srcRect = { (int)xPos, (int)yPos, 1024, 1024 };
	WRect dstRect = {rect.x + 2, rect.y + 2, 252, 252};
	spritesheet->Blit(&srcRect, &dstRect);

	int realTileWidth = tilewidth / (srcRect.w / dstRect.w);
	int realTileHeight = tileheight / (srcRect.h / dstRect.h);

	if (GUI::Button({ rect.x, 278, 64, 64 }, "A"))
	{
		layer = 0;
	}
	if (GUI::Button({ rect.x + 64, 278, 64, 64 }, "B"))
	{
		layer = 1;
	}
	if (GUI::Button({ rect.x + 128, 278, 64, 64 }, "C"))
	{
		layer = 2;
	}

	if(selRectPos.x == -100) selRectPos = { dstRect.x, dstRect.y, realTileWidth, realTileHeight };

	float scaledX = (float)xPos / ((float)srcRect.w / (float)dstRect.w);
	float scaledY = (float)yPos / ((float)srcRect.h / (float)dstRect.h);
	// Select tile
	if (Collision::AABB(Mouse::position, dstRect))
	{
		if (Mouse::KeyReleased(0))
		{
			int xPos = (Mouse::position.x - dstRect.x + scaledX) / realTileWidth;
			int yPos = (Mouse::position.y - dstRect.y + scaledY) / realTileHeight;
			selected = xPos + yPos * (spritesheet->size.x / tilewidth);
			
			selRectPos.x = xPos * realTileWidth + dstRect.x;
			selRectPos.y = yPos * realTileHeight + dstRect.y;
			selRectPos.w = realTileWidth;
			selRectPos.h = realTileHeight;
		}
		if (Mouse::KeyClicked(1))
		{
			initialMousePos = Mouse::position;
			initialMapPos.x = xPos;
			initialMapPos.y = yPos;
		}
		if (Mouse::KeyDown(1))
		{
			xPos = initialMapPos.x - (Mouse::position.x - initialMousePos.x) * (srcRect.w / dstRect.w);
			yPos = initialMapPos.y - (Mouse::position.y - initialMousePos.y) * (srcRect.h / dstRect.h);
		}
	}
	WRect srp = selRectPos;
	srp.x -= scaledX;
	srp.y -= scaledY;

	WRect ssrcRect = *selectionRect->rect;
	if (srp.x < rect.x)
	{
		ssrcRect.x -= srp.x - rect.x;
		srp.x += ssrcRect.x;
		srp.w -= ssrcRect.x;
	}
	if (srp.y + srp.h > rect.y + rect.h)
	{
		ssrcRect.y += (srp.y + srp.h) - (rect.y + rect.h);
		srp.y += ssrcRect.y;
		srp.h -= ssrcRect.y;
	}
	selectionRect->Blit(&ssrcRect, &srp);

	xPos = GUI::HorizontalScrollBar({ rect.x, rect.y + rect.h, rect.w, 22 }, xPos, spritesheet->size.x - srcRect.w);
	yPos = GUI::VerticalScrollBar({ rect.x + rect.w, rect.y, 22, rect.h }, yPos, spritesheet->size.y - srcRect.h);
}

void Editor::Exit()
{
	delete spritesheet;
	delete gridtex;
	delete map;
	delete grid;
}