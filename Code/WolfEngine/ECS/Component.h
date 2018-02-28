#ifndef _COMPONENT_H
#define _COMPONENT_H
#include <string>

class GameObject;

///	The baseclass for components
class Component
{
	public:
        ///    A pointer to the GameObject that contains this component
        GameObject* gameObject;
        /// The name of the GameObject that contains this component
        std::string name;
    
		///	Runs when a component is first added to a GameObject
		virtual void Added()
		{

		}
		///	Runs every frame
		virtual void Update()
		{

		}
		/// Runs every frame, but after Update and the renderer
		virtual void LateUpdate()
		{

		}

		/// Runs when the component was removed, or the GameObject deleted
		virtual void Destroy()
		{

		}
};
#endif
