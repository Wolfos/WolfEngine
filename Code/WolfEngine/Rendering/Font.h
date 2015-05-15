#ifndef _FONT_H
#define _FONT_H
#include "../Includes.h"
#include <string>

class Font
{
	public:
		Font(std::string filename, int size);
		~Font();

		void Blit(int x, int y, std::string text, SDL_Color color);

	private:
		TTF_Font* font;
};
#endif