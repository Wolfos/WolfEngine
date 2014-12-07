#include "GUI.h"
#include "../Input/Mouse.h"

std::unordered_map<const std::type_info*, Window*> GUI::windows;

void GUI::Update()
{
	Mouse::overGUI = false;
	for (unsigned i = 0; i != windows.bucket_count(); ++i)
	{
		for (auto local_it = windows.begin(i); local_it != windows.end(i); ++local_it)
		{
			local_it->second->Render();
			local_it->second->Update();
		}
	}
}