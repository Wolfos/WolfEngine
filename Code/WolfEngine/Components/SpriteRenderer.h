#ifndef _SPRITERENDERER_H
#define _SPRITERENDERER_H
#include "../ECS/Component.h"
#include "../Includes.h"
#include <string>
#include "../Rendering/Bitmap.h"
#include "../Rendering/Mesh.h"
#include "../Rendering/Shader.h"
#include "Camera.h"

///
///	A component for rendering sprites from a sheet or image
///
class SpriteRenderer : public Component
{
public:
	///	The spritesheet or sprite (if no sheet is needed)
	Bitmap* spriteSheet;
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
	virtual void Destroy();

	///	Loads a file into the spritesheet
	void Load(std::string filename, int frameWidth = 0, int frameHeight = 0);

	///	Renders the sprite
	void Render(Camera* camera);

	Shader* shader;

	// Spritesheet width in frames, in case not the entire spritesheet is being used you can set this manually
	int widthInFrames = 1;
private:
	int sheetwidth, sheetheight;
	Mesh* mesh;
	static Shader* defaultShader;

};
#endif
