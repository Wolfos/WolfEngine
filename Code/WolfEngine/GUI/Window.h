#ifndef _WINDOW_H
#define _WINDOW_H
#include "../Includes.h"
#include "../Models/Point.h"

class Window
{
	public:
		//Makes the background, and calculates the hitbox for mouseover and mousedown
		Window(int x, int y, int width, int height);
		Point position;
		///Renders the background and handles mouseOver and mouseDown events
		void Render();
		///	Runs every frame
		virtual void Update()
		{

		}
		SDL_Rect hitbox;
		bool clicked = false;
		bool mouseOver = false;
	private:
		SDL_Texture* background;
		bool Collide(Point point, SDL_Rect rect);
};

#endif