#include "Editor.h"


void Editor::Start()
{
	spritesheet = new Bitmap("Terrain.png");
	gridtex = new Bitmap("Grid.png");
	selectionRect = new Bitmap("SelectionRect.png");

	map = new Map(100, 100, 3, -1, mapScale);
	grid = new Map(100, 100, 1, 0, mapScale);


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
	else if (!Mouse::KeyDown(0) && !Mouse::KeyDown(1) && !Mouse::KeyDown(2)) canDraw = true;
	// Determine if the mouse is over the map
	if 
	(	Mouse::position.x + cam->position.x >= 0 && 
		Mouse::position.x + cam->position.x <= map->width * tilewidth && 
		Mouse::position.y + cam->position.y >= 0 && 
		Mouse::position.y + cam->position.y <= map->height * tileheight)
	{
		if (canDraw) // Did we start the click action on a GUI element?
		{
			// Determine the coordinates of the tile the cursor is over
			int xMPos = (Mouse::position.x + cam->position.x) / ((float)tilewidth * mapScale);
			int yMPos = (Mouse::position.y + cam->position.y) / ((float)tileheight * mapScale);
			// LMB places a tile, RMB removes one
			if(Mouse::KeyDown(0)) map->Put(xMPos, yMPos, layer, selected);
			if(Mouse::KeyDown(1)) map->Put(xMPos, yMPos, layer, -1);

			if (Mouse::KeyClicked(2))
			{
				initialMousePos = Mouse::position;
				initialCamPos.x = cam->position.x;
				initialCamPos.y = cam->position.y;
			}
			if (Mouse::KeyDown(2))
			{
				cam->position.x = initialCamPos.x - (Mouse::position.x - initialMousePos.x);
				cam->position.y = initialCamPos.y - (Mouse::position.y - initialMousePos.y);
			}
		}
	}

	// Keyboard shortcuts
	if (Keyboard::KeyClicked(Keys::G)) showGrid = !showGrid;

	if (Keyboard::KeyClicked(Keys::One)) layer = 0;
	if (Keyboard::KeyClicked(Keys::Two)) layer = 1;
	if (Keyboard::KeyClicked(Keys::Three)) layer = 2;

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

	bool highlight = layer == 0;
	if (GUI::Button({ rect.x, 278, 64, 64 }, "A", highlight))
	{
		layer = 0;
	}
	highlight = layer == 1;
	if (GUI::Button({ rect.x + 64, 278, 64, 64 }, "B", highlight))
	{
		layer = 1;
	}
	highlight = layer == 2;
	if (GUI::Button({ rect.x + 128, 278, 64, 64 }, "C", highlight))
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
		if (Mouse::KeyClicked(2))
		{
			initialMousePos = Mouse::position;
			initialTileSelectPos.x = xPos;
			initialTileSelectPos.y = yPos;
		}
		if (Mouse::KeyDown(2))
		{
			xPos = initialTileSelectPos.x - (Mouse::position.x - initialMousePos.x) * (srcRect.w / dstRect.w);
			yPos = initialTileSelectPos.y - (Mouse::position.y - initialMousePos.y) * (srcRect.h / dstRect.h);
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
