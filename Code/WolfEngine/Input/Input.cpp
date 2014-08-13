/*
WolfEngine © 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Input.h"
#include "Mouse.h"

Keys Input::keys;

void Input::Update(SDL_Event * eventHandler)
{
	Mouse::key1.released = false;
	Mouse::key2.released = false;
	Mouse::key3.released = false;

	while (SDL_PollEvent(eventHandler) != 0)
	{
		if (eventHandler->type == SDL_KEYDOWN)
		{
			switch (eventHandler->key.keysym.sym)
			{
				//Letter Keys
			case SDLK_a:
				keys.A = 1;
				break;
			case SDLK_b:
				keys.B = 1;
				break;
			case SDLK_c:
				keys.C = 1;
				break;
			case SDLK_d:
				keys.D = 1;
				break;
			case SDLK_e:
				keys.E = 1;
				break;
			case SDLK_f:
				keys.F = 1;
				break;
			case SDLK_g:
				keys.G = 1;
				break;
			case SDLK_h:
				keys.H = 1;
				break;
			case SDLK_i:
				keys.I = 1;
				break;
			case SDLK_j:
				keys.J = 1;
				break;
			case SDLK_k:
				keys.K = 1;
				break;
			case SDLK_l:
				keys.L = 1;
				break;
			case SDLK_m:
				keys.M = 1;
				break;
			case SDLK_n:
				keys.N = 1;
				break;
			case SDLK_o:
				keys.O = 1;
				break;
			case SDLK_p:
				keys.P = 1;
				break;
			case SDLK_q:
				keys.Q = 1;
				break;
			case SDLK_r:
				keys.R = 1;
				break;
			case SDLK_s:
				keys.S = 1;
				break;
			case SDLK_t:
				keys.T = 1;
				break;
			case SDLK_u:
				keys.U = 1;
				break;
			case SDLK_v:
				keys.V = 1;
				break;
			case SDLK_w:
				keys.W = 1;
				break;
			case SDLK_x:
				keys.X = 1;
				break;
			case SDLK_y:
				keys.Y = 1;
				break;
			case SDLK_z:
				keys.Z = 1;
				break;
				//Bottom row
			case SDLK_LSHIFT:
				keys.LeftShift = 1;
				break;
			case SDLK_LCTRL:
				keys.LeftControl = 1;
				break;
			case SDLK_LALT:
				keys.LeftAlt = 1;
				break;
			case SDLK_SPACE:
				keys.Space = 1;
				break;
			case SDLK_RALT:
				keys.RightAlt = 1;
				break;
			case SDLK_RCTRL:
				keys.RightControl = 1;
				break;
			case SDLK_RSHIFT:
				keys.RightShift = 1;
				break;

				//Arrow Keys
			case SDLK_UP:
				keys.ArrowUp = 1;
				break;
			case SDLK_LEFT:
				keys.ArrowLeft = 1;
				break;
			case SDLK_DOWN:
				keys.ArrowDown = 1;
				break;
			case SDLK_RIGHT:
				keys.ArrowRight = 1;
				break;

				//Other Keys
			case SDLK_RETURN:
				keys.Return = 1;
				break;
			case SDLK_RETURN2:
				keys.KeypadReturn = 1;
				break;
			case SDLK_TAB:
				keys.Tab = 1;
				break;
			}
		}
		if (eventHandler->type == SDL_KEYUP)
		{
			switch (eventHandler->key.keysym.sym)
			{
				//Letter Keys
			case SDLK_a:
				keys.A = 0;
				break;
			case SDLK_b:
				keys.B = 0;
				break;
			case SDLK_c:
				keys.C = 0;
				break;
			case SDLK_d:
				keys.D = 0;
				break;
			case SDLK_e:
				keys.E = 0;
				break;
			case SDLK_f:
				keys.F = 0;
				break;
			case SDLK_g:
				keys.G = 0;
				break;
			case SDLK_h:
				keys.H = 0;
				break;
			case SDLK_i:
				keys.I = 0;
				break;
			case SDLK_j:
				keys.J = 0;
				break;
			case SDLK_k:
				keys.K = 0;
				break;
			case SDLK_l:
				keys.L = 0;
				break;
			case SDLK_m:
				keys.M = 0;
				break;
			case SDLK_n:
				keys.N = 0;
				break;
			case SDLK_o:
				keys.O = 0;
				break;
			case SDLK_p:
				keys.P = 0;
				break;
			case SDLK_q:
				keys.Q = 0;
				break;
			case SDLK_r:
				keys.R = 0;
				break;
			case SDLK_s:
				keys.S = 0;
				break;
			case SDLK_t:
				keys.T = 0;
				break;
			case SDLK_u:
				keys.U = 0;
				break;
			case SDLK_v:
				keys.V = 0;
				break;
			case SDLK_w:
				keys.W = 0;
				break;
			case SDLK_x:
				keys.X = 0;
				break;
			case SDLK_y:
				keys.Y = 0;
				break;
			case SDLK_z:
				keys.Z = 0;
				break;
				//Bottom row
			case SDLK_LSHIFT:
				keys.LeftShift = 0;
				break;
			case SDLK_LCTRL:
				keys.LeftControl = 0;
				break;
			case SDLK_LALT:
				keys.LeftAlt = 0;
				break;
			case SDLK_SPACE:
				keys.Space = 0;
				break;
			case SDLK_RALT:
				keys.RightAlt = 0;
				break;
			case SDLK_RCTRL:
				keys.RightControl = 0;
				break;
			case SDLK_RSHIFT:
				keys.RightShift = 0;
				break;

				//Arrow Keys
			case SDLK_UP:
				keys.ArrowUp = 0;
				break;
			case SDLK_LEFT:
				keys.ArrowLeft = 0;
				break;
			case SDLK_DOWN:
				keys.ArrowDown = 0;
				break;
			case SDLK_RIGHT:
				keys.ArrowRight = 0;
				break;

				//Other Keys
			case SDLK_RETURN:
				keys.Return = 0;
				break;
			case SDLK_RETURN2:
				keys.KeypadReturn = 0;
				break;
			case SDLK_TAB:
				keys.Tab = 0;
				break;
			}
		}
		Mouse::Update(eventHandler);
	}
}