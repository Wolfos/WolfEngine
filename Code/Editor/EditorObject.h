//
//  EditorComponent.h
//  WolfEngine
//
//  Created by Robin on 19/12/2017.
//

#pragma once
#include "../WolfEngine/API.h"
#include "EditorComponent.h"

class EditorObject : public Component
{
public:
    std::vector<EditorComponent> components;
};
