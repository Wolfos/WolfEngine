#include "Bitmap.h"

#include "../WolfEngine.h"

// Exclude support for image formats that I'm unlikely to use
// Just remove one if you want to use any of these
#define STBI_NO_BMP
#define STBI_NO_TGA
#define STBI_NO_GIF
#define STBI_NO_HDR
#define STBI_NO_PIC
#define STBI_NO_PNM
// Somewhat more friendly error logs from STBI
#define STBI_FAILURE_USERMSG

#define STB_IMAGE_IMPLEMENTATION
#include "stb_image.h"

std::vector<Bitmap*> Bitmap::cache;

Bitmap::Bitmap(std::string file)
{
	bool cached = false;
	filename = file;
	for (size_t i = 0; i < cache.size(); i++)
	{
		if (cache[i]->filename == filename)
		{
			cached = true;
			cache[i]->count++;
			count = cache[i]->count;
			textureID = cache[i]->textureID;
			size = cache[i]->size;
		}
	}

	if (!cached)
	{
		count = new int;

		int width, height, channels;
		std::string path = WolfEngine::FindAssetFolder() + "Sprites/" + filename;
		unsigned char* image = stbi_load(path.c_str(), &width, &height, &channels, STBI_rgb_alpha);

		if(image == nullptr) // Failure
		{
			printf("Image '%s' could not be loaded because: %s \n", filename.c_str(), stbi_failure_reason());
		}

		size = {width, height};
		GenTexture(image, channels > 3, width, height);
		stbi_image_free(image);

		cache.push_back(this);
	}

	rect = new WRect;

	rect->x = 0;
	rect->y = 0;
	rect->w = size.x;
	rect->h = size.y;
}

Bitmap::~Bitmap()
{
	*count--;
	if (*count <= 0)
	{
		//SDL_DestroyTexture(texture);
		for (size_t i = 0; i < cache.size(); i++)
		{
			if (cache[i]->filename == filename)
			{
				cache.erase(cache.begin() + i);
				break;
			}
		}
		delete count;
	}
	delete rect;
}


void Bitmap::GenTexture(unsigned char* image, bool hasAlpha, int width, int height)
{
	glGenTextures(1, &textureID);
	glBindTexture(GL_TEXTURE_2D, textureID);

	// Clamp
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_BORDER);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_BORDER);
	// Point filtering
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);

	glTexImage2D(GL_TEXTURE_2D, 0, hasAlpha ? GL_RGBA : GL_RGB, width, height, 0, hasAlpha ? GL_RGBA : GL_RGB, GL_UNSIGNED_BYTE, image);
}
