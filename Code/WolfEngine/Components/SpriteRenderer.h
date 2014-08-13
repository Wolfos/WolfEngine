#ifndef _SPRITERENDERER_H
#define _SPRITERENDERER_H
#include "../ECS/Component.h"
#include "../Includes.h"
#include <string>

///
///	A component for rendering sprites from a sheet or image
///
class SpriteRenderer : public Component
{
public:
	///	The spritesheet or sprite (if no sheet is needed)
	SDL_Texture* spriteSheet;
	///	Filename, not actually used at the moment
	char* filename;
	///	The individual sprite's width (not the spritesheet's)
	int frameWidth = 0;
	///	The individual sprite's height (not the spritesheet's)
	int frameHeight = 0;
	///	The frame we're at (0 to however many there might be on the spritesheet), can animate
	int frame = 0;
	///	Blank space between sprites on the sheet
	int sheetOffset = 0;
	///	The layer to render on
	int layer = 0;
	/// The sprite's pivot, standard is dead in the center
	SDL_Point* center;
	virtual void Added();
	virtual void Update();

	///	Loads a file into the spritesheet
	void Load(std::string filename);

	///	Renders the sprite
	void Render();
private:
	int sheetwidth, sheetheight;
	SDL_Rect* clip;
};
#endif