#ifndef _WINDOW_H
#define _WINDOW_H
#include "../Includes.h"
#include "../Rendering/Bitmap.h"
#include "../Models/Point.h"

enum WindowSnapState{
	None, Top, Bottom, Left, Right
};

class Window
{
	public:
		/// Makes the background, and calculates the hitbox for mouseover and mousedown
		Window(int x, int y, int width, int height);
		Point position;
		/// Renders the background and handles mouseOver and mouseDown events
		void Render();
		///	Runs every frame
		virtual void Update()
		{

		}

		WindowSnapState vertSnap = None;
		WindowSnapState horSnap = None;

		Rect hitbox;
		bool clicked = false;
		bool mouseOver = false;
	private:
		Bitmap* background;
		Bitmap* titleBar;
		int barHeight = 20;
		Rect barRect;
		void HandleInput();
		bool Collide(Point point, Rect rect);
		bool dragging = false;
		Point startPos; // For dragging
		Point startMousePos;
};


#endif