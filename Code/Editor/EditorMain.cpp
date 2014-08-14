#include "EditorMain.h"

void EditorMain::Start()
{
	spritesheet = Image::Load("Terrain.png");
	gridtex = Image::Load("Grid.png");
	map = new Map(10, 10, 3, -1);

	grid = new Map(10, 10, 1, 0);
	
	tilewidth = 128;
	tileheight = 128;

	tilePicker = new TilePicker(Screen::mainCamera->width-256, 0, 256, 256);

	camera = Screen::mainCamera->gameObject->transform;

}

void EditorMain::Update()
{
	int xMPos = (Mouse::position.x + camera->position.x) / tilewidth;
	int yMPos = (Mouse::position.y + camera->position.y) / tileheight;

	int selected = tilePicker->selected;

	map->Render(Screen::mainCamera->screen, 0, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
	map->Render(Screen::mainCamera->screen, 1, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
	map->Render(Screen::mainCamera->screen, 2, spritesheet, tilewidth, tileheight, 0, camera->gameObject);

	
		
	if(showGrid) grid->Render(Screen::mainCamera->screen, 0, gridtex, tilewidth, tileheight, 0, Screen::mainCamera->gameObject);


	//Controls
	if (Mouse::KeyDown(1) && !tilePicker->mouseOver && Mouse::position.x + camera->position.x >= 0 && Mouse::position.x + camera->position.x <= map->width * tilewidth && Mouse::position.y + camera->position.y >= 0 && Mouse::position.y + camera->position.y <= map->height * tileheight)
	{
		map->Put(xMPos, yMPos, layer, selected);
	}

	if (Keyboard::KeyClicked(Keys::G)) showGrid = !showGrid;

	if (Keyboard::KeyClicked(Keys::A)) layer = 0;
	if (Keyboard::KeyClicked(Keys::B)) layer = 1;
	if (Keyboard::KeyClicked(Keys::C)) layer = 2;

	if (Keyboard::KeyDown(Keys::W)) camera->Move(0, -10);
	if (Keyboard::KeyDown(Keys::A)) camera->Move(-10, 0);
	if (Keyboard::KeyDown(Keys::S)) camera->Move(0, 10);
	if (Keyboard::KeyDown(Keys::D)) camera->Move(10, 0);
}

void EditorMain::Exit()
{

}