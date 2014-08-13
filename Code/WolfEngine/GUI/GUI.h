#ifndef _GUI_H
#define _GUI_H

#include <unordered_map>
#include <typeinfo> 
#include "Window.h"

class GUI
{
	private:
		static std::unordered_map<const std::type_info*, Window*> windows;
	public:
		template <typename W>
		static void AddWindow(W* window)
		{
			windows[&typeid(*window)] = window;
		}

		static void Update();
};

#endif