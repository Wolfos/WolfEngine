#ifndef _BITMAP_H
#define _BITMAP_H
#include "../Includes.h"
#include "../Math/Vectors.h"
#include <string>
#include <vector>
class Bitmap
{
public:
	std::string filename;
	int* count = 0;
	GLuint textureID;
	Vector2<int> size;
	WRect* rect;

	Bitmap(std::string file);
	~Bitmap();

	void Blit(WRect* srcrect, WRect* dstrect, double angle = 0, SDL_Point* center = NULL, float scale = 1);
private:
	SDL_Surface* LoadSurface(std::string file);
	void GenTexture(SDL_Surface* surface);
	static std::vector<Bitmap*> cache;
};

#endif