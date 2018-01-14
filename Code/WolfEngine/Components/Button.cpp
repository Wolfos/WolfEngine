#include "Button.h"
#include "../ECS/GameObject.h"
#include "../Models/Point.h"
#include "../Input/Input.h"
#include "../Input/Mouse.h"
#include "../Utilities/Collision.h"

void Button::Added()
{
	transform = gameObject->GetComponent<Transform>();
	renderer = gameObject->GetComponent<SpriteRenderer>();

#ifndef __ANDROID__
	if (!renderer) throw "No SpriteRenderer is present on the GameObject";
#endif

	hitBox.x = transform->GetPosition().x;
	hitBox.y = transform->GetPosition().y;
	hitBox.w = renderer->frameWidth * transform->localScale.x;
	hitBox.h = renderer->frameHeight * transform->localScale.y;
}

void Button::Update()
{
	if (Mouse::KeyClicked(1) && Collision::AABB(Mouse::position, hitBox))
	{
		clicked = true;
	}
	else if (Collision::AABB(Mouse::position, hitBox))
	{
		clicked = false;
		mouseOver = true;
	}
	else
	{
		clicked = false;
		mouseOver = false;
	}
}
