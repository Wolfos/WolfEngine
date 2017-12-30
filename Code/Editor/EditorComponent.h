//
//  EditorComponent.h
//  WolfEngine
//
//  Created by Robin on 19/12/2017.
//

#pragma once
#include "../WolfEngine/API.h"

struct EditorComponent
{
    std::string name;
    std::vector<std::string> stringMembers;
    std::vector<int> intMembers;
    std::vector<WPoint> pointMembers;
    std::vector<WPointF> pointFMembers;
};
