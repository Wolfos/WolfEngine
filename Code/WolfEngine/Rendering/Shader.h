//
// Created by Robin on 01/01/2018.
//
#pragma once
#include "../Includes.h"
#include <string>

class Shader
{
public:
	GLuint id;
	Shader(std::string vertFilename, std::string fragFilename);
	~Shader();
};
