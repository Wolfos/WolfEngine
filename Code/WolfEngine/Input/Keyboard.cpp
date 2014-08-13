#include "Keyboard.h"

std::vector<Key> Keyboard::keys;

void Keyboard::Init()
{
	for (int i = 0; i < Keys::NUM_KEYS; i++)
	{
		Key key;

		key.clicked = false;
		key.down = false;
		key.released = false;
		key.wasdown = false;

		keys.push_back(key);
	}
}

bool Keyboard::KeyDown(int key)
{
	if (keys[key].down) return true;
	else return false;
}

bool Keyboard::KeyReleased(int key)
{
	if (keys[key].released) return true;
	else return false;
}

bool Keyboard::KeyClicked(int key)
{
	if (keys[key].released) return true;
	else return false;
}

void Keyboard::Down(int key)
{
	keys[key].down = true;
	if (!keys[key].wasdown) keys[key].clicked = true;
	else keys[key].clicked = false;
}

void Keyboard::Up(int key)
{
	if (!keys[key].released) keys[key].released = true;
	keys[key].down = false;
	keys[key].clicked = false;
}


void Keyboard::Update(SDL_Event* eventHandler)
{
	for (int i = 0; i < Keys::NUM_KEYS; i++)
	{
		if (keys[i].down) keys[i].wasdown = true;
		else keys[i].wasdown = false;
	}

	#pragma region KEYDOWN
	if (eventHandler->type == SDL_KEYDOWN)
	{
		switch (eventHandler->key.keysym.sym)
		{
			case SDLK_a:
				Down(Keys::A);
				break;
			case SDLK_b:
				Down(Keys::B);
				break;
			case SDLK_c:
				Down(Keys::C);
				break;
			case SDLK_d:
				Down(Keys::D);
				break;
			case SDLK_e:
				Down(Keys::E);
				break;
			case SDLK_f:
				Down(Keys::F);
				break;
			case SDLK_g:
				Down(Keys::G);
				break;
			case SDLK_h:
				Down(Keys::H);
				break;
			case SDLK_i:
				Down(Keys::I);
				break;
			case SDLK_j:
				Down(Keys::J);
				break;
			case SDLK_k:
				Down(Keys::K);
				break;
			case SDLK_l:
				Down(Keys::L);
				break;
			case SDLK_m:
				Down(Keys::M);
				break;
			case SDLK_n:
				Down(Keys::N);
				break;
			case SDLK_o:
				Down(Keys::O);
				break;
			case SDLK_p:
				Down(Keys::P);
				break;
			case SDLK_q:
				Down(Keys::Q);
				break;
			case SDLK_r:
				Down(Keys::R);
				break;
			case SDLK_s:
				Down(Keys::S);
				break;
			case SDLK_t:
				Down(Keys::T);
				break;
			case SDLK_u:
				Down(Keys::U);
				break;
			case SDLK_v:
				Down(Keys::V);
				break;
			case SDLK_w:
				Down(Keys::W);
				break;
			case SDLK_x:
				Down(Keys::X);
				break;
			case SDLK_y:
				Down(Keys::Y);
				break;
			case SDLK_z:
				Down(Keys::Z);
				break;

			//Bottom row
			case SDLK_LSHIFT:
				Down(Keys::LeftShift);
				break;
			case SDLK_LCTRL:
				Down(Keys::LeftControl);
				break;
			case SDLK_LALT:
				Down(Keys::LeftAlt);
				break;
			case SDLK_SPACE:
				Down(Keys::Space);
				break;
			case SDLK_RALT:
				Down(Keys::RightAlt);
				break;
			case SDLK_RCTRL:
				Down(Keys::RightControl);
				break;
			case SDLK_RSHIFT:
				Down(Keys::RightShift);
				break;

			//Arrow Keys
			case SDLK_UP:
				Down(Keys::ArrowUp);
				break;
			case SDLK_LEFT:
				Down(Keys::ArrowLeft);
				break;
			case SDLK_DOWN:
				Down(Keys::ArrowDown);
				break;
			case SDLK_RIGHT:
				Down(Keys::ArrowRight);
				break;

			//Other Keys
			case SDLK_RETURN:
				Down(Keys::Return);
				break;
			case SDLK_RETURN2:
				Down(Keys::KeypadReturn);
				break;
			case SDLK_TAB:
				Down(Keys::Tab);
				break;
		}
	}
	#pragma endregion

	#pragma region KEYUP
	if (eventHandler->type == SDL_KEYUP)
	{
		switch (eventHandler->key.keysym.sym)
		{
		case SDLK_a:
			Up(Keys::A);
			break;
		case SDLK_b:
			Up(Keys::B);
			break;
		case SDLK_c:
			Up(Keys::C);
			break;
		case SDLK_d:
			Up(Keys::D);
			break;
		case SDLK_e:
			Up(Keys::E);
			break;
		case SDLK_f:
			Up(Keys::F);
			break;
		case SDLK_g:
			Up(Keys::G);
			break;
		case SDLK_h:
			Up(Keys::H);
			break;
		case SDLK_i:
			Up(Keys::I);
			break;
		case SDLK_j:
			Up(Keys::J);
			break;
		case SDLK_k:
			Up(Keys::K);
			break;
		case SDLK_l:
			Up(Keys::L);
			break;
		case SDLK_m:
			Up(Keys::M);
			break;
		case SDLK_n:
			Up(Keys::N);
			break;
		case SDLK_o:
			Up(Keys::O);
			break;
		case SDLK_p:
			Up(Keys::P);
			break;
		case SDLK_q:
			Up(Keys::Q);
			break;
		case SDLK_r:
			Up(Keys::R);
			break;
		case SDLK_s:
			Up(Keys::S);
			break;
		case SDLK_t:
			Up(Keys::T);
			break;
		case SDLK_u:
			Up(Keys::U);
			break;
		case SDLK_v:
			Up(Keys::V);
			break;
		case SDLK_w:
			Up(Keys::W);
			break;
		case SDLK_x:
			Up(Keys::X);
			break;
		case SDLK_y:
			Up(Keys::Y);
			break;
		case SDLK_z:
			Up(Keys::Z);
			break;

			//Bottom row
		case SDLK_LSHIFT:
			Up(Keys::LeftShift);
			break;
		case SDLK_LCTRL:
			Up(Keys::LeftControl);
			break;
		case SDLK_LALT:
			Up(Keys::LeftAlt);
			break;
		case SDLK_SPACE:
			Up(Keys::Space);
			break;
		case SDLK_RALT:
			Up(Keys::RightAlt);
			break;
		case SDLK_RCTRL:
			Up(Keys::RightControl);
			break;
		case SDLK_RSHIFT:
			Up(Keys::RightShift);
			break;

			//Arrow Keys
		case SDLK_UP:
			Up(Keys::ArrowUp);
			break;
		case SDLK_LEFT:
			Up(Keys::ArrowLeft);
			break;
		case SDLK_DOWN:
			Up(Keys::ArrowDown);
			break;
		case SDLK_RIGHT:
			Up(Keys::ArrowRight);
			break;

			//Other Keys
		case SDLK_RETURN:
			Up(Keys::Return);
			break;
		case SDLK_RETURN2:
			Up(Keys::KeypadReturn);
			break;
		case SDLK_TAB:
			Up(Keys::Tab);
			break;
		}
	}
	#pragma endregion
}