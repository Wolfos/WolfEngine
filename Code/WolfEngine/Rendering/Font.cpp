#include "Font.h"
#include "../WolfEngine.h"

Font::Font(std::string filename, int size)
{
	std::string path = WolfEngine::FindAssetFolder() + "Fonts/" + filename;
	font = TTF_OpenFont(path.c_str(), size);
	if (font == NULL)
	{
		printf("Failed to load font! SDL_ttf Error: %s\n", TTF_GetError());
	}
}

Font::~Font()
{
	TTF_CloseFont(font);
}

void Font::Blit(int x, int y, std::string text, SDL_Color color)
{
	SDL_Surface* textSurface = TTF_RenderText_Blended(font, text.c_str(), color);
	if (textSurface == NULL)
	{
		printf("Unable to render text surface! SDL_ttf Error: %s\n", TTF_GetError());
	}

	//SDL_Texture* mTexture = SDL_CreateTextureFromSurface(WolfEngine::renderer, textSurface);
	//if (mTexture == NULL)
	//{
		//printf("Unable to create texture from rendered text! SDL Error: %s\n", SDL_GetError());
	//}

	//WRect srcRect = { 0, 0, textSurface->w, textSurface->h };
	//WRect dstRect = { x - textSurface->w/2, y - textSurface->h/2, textSurface->w, textSurface->h };

	//SDL_RenderCopy(WolfEngine::renderer, mTexture, &srcRect, &dstRect);

	SDL_FreeSurface(textSurface);
	//SDL_DestroyTexture(mTexture);
}