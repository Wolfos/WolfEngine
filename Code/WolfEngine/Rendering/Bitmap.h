#ifndef _BITMAP_H
#define _BITMAP_H
#include "../Includes.h"
#include "../Models/Point.h"
#include <string>
#include <vector>
class Bitmap
{
public:
	std::string filename;
	int* count = 0;
	Texture* texture;
	WPoint size;
	WRect* rect;

	Bitmap(std::string file);
	~Bitmap();

	void Blit(WRect* srcrect, WRect* dstrect, double angle = 0, SDL_Point* center = NULL);
private:
	SDL_Surface* LoadSurface(std::string file);
	Texture* ToTexture(SDL_Surface* surface);
	static std::vector<Bitmap*> cache;
};

#endif