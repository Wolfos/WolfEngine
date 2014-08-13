#ifndef _SCREEN_H
#define _SCREEN_H
#include "../Components/Camera.h"
///
///	Contains some objects that are useful for rendering
///
class Screen
{
	public:
		/// The main camera
		static Camera* mainCamera;
		/// Amount of layers in use
		static int layers;
};
#endif