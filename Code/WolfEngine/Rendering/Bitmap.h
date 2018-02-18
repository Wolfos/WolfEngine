#ifndef _BITMAP_H
#define _BITMAP_H
#include "../Includes.h"
#include "../Math/Vectors.h"
#include <string>
#include <vector>
class Bitmap
{
public:
	/// OpenGL texture ID, shared between all Bitmap objects that use the same image file
	GLuint textureID;
	Vector2<int> size;
	/// Size as a rectangle
	// TODO: this isn't necessary, remove it once we have a new UI system
	WRect* rect;

	/// Create a new Bitmap object from a file
	Bitmap(std::string file);
	~Bitmap();

private:
	std::string filename;
	// How many Bitmap objects use this textureID?
	int* count = 0;
	void GenTexture(unsigned char* image, bool hasAlpha, int width, int height);
	static std::vector<Bitmap*> cache;
};

#endif