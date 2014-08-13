#ifndef _GAMEOBJECT_H
#define _GAMEOBJECT_H
#include "Component.h"
#include "../Components/Transform.h"
#include <typeinfo> 
#include <unordered_map>

///
///	A GameObject is a framework to put Components on
///	It is advisable to make them through ObjectManager::NewGameObject (unless you want to update them by yourself, which you don't)
///
class GameObject
{
	private:
		std::unordered_map<const std::type_info*, Component*> components;
	public:
		/// A pointer to the GameObject's transform component (every GameObject gets a transform component by default)
		Transform* transform;
		/// The GameObject's name
		char* name;
		/// The GameObject's unique ID
		int id;

		///	Runs every frame, runs the Update() function for each component
		void Update();
		///	Runs every frame, runs the LateUpdate() function for each component
		void LateUpdate();

		GameObject();
		~GameObject();

		///	Template, returns a component of type <C>
		template <typename C>
		C* GetComponent()
		{
			if (components.count(&typeid(C)) != 0)
			{
				return static_cast<C*>(components[&typeid(C)]);
			}
			else
			{
				return 0;
			}
		}

		///	Template, adds a component of type <C>
		template <typename C>
		C* AddComponent()
		{
			C* component = new C;
			components[&typeid(*component)] = component;
			component->gameObject = this;
			component->Added();
			return component;
		}

		/// Template, removes a component of type <C>
		/// Doesn't do anything if the component doesn't exist
		template <typename C>
		void RemoveComponent()
		{
			if (components.count(&typeid(C)) != 0)
			{
				C* component = components[&typeid(C)];
				components.erase(component);
				free(component);
			}
		}


};
#endif