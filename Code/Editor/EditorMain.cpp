#include "EditorMain.h"

void EditorMain::Start()
{
	spritesheet = Image::Load("Terrain.png");
	gridtex = Image::Load("Grid.png");
	map = new Map(10, 10, 3, -1);

	grid = new Map(10, 10, 1, 0);
	
	tilewidth = 128;
	tileheight = 128;

	//tilePicker = ObjectManager::NewGameObject("TilePicker");
	//tilePicker->transform->position = { Screen::mainCamera->width - 256, 0 };
	//tilePicker->AddComponent<TilePicker>();

	tilePicker = new TilePicker(Screen::mainCamera->width-256, 0, 256, 256);

	camera = Screen::mainCamera->gameObject->transform;

}

void EditorMain::Update()
{
	//tilePicker->transform->position = { Screen::mainCamera->width - 256, 0 };

	int xMPos = (Mouse::position.x + camera->position.x) / tilewidth;
	int yMPos = (Mouse::position.y + camera->position.y) / tileheight;

	int selected = tilePicker->selected;

	map->Render(Screen::mainCamera->screen, 0, spritesheet, tilewidth, tileheight, 0, Screen::mainCamera->gameObject);
	map->Render(Screen::mainCamera->screen, 1, spritesheet, tilewidth, tileheight, 0, Screen::mainCamera->gameObject);
	map->Render(Screen::mainCamera->screen, 2, spritesheet, tilewidth, tileheight, 0, Screen::mainCamera->gameObject);

	if(Input::keys.G) grid->Render(Screen::mainCamera->screen, 0, gridtex, tilewidth, tileheight, 0, Screen::mainCamera->gameObject);

	if (Mouse::KeyReleased(1) && !tilePicker->mouseOver)
	{
		map->Put(xMPos, yMPos, layer, selected);
	}

	if (Input::keys.A) layer = 0;
	if (Input::keys.B) layer = 1;
	if (Input::keys.C) layer = 2;

	if (Input::keys.W) Screen::mainCamera->gameObject->transform->Move(0, -5);
	if (Input::keys.A) Screen::mainCamera->gameObject->transform->Move(-5, 0);
	if (Input::keys.S) Screen::mainCamera->gameObject->transform->Move(0, 5);
	if (Input::keys.D) Screen::mainCamera->gameObject->transform->Move(5, 0);
}

void EditorMain::Exit()
{

}