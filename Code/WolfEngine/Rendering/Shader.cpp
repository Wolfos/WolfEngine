//
// Created by Robin on 01/01/2018.
//

#include "Shader.h"
#include "../WolfEngine.h"
#include <fstream>

Shader::Shader(std::string vertFilename, std::string fragFilename)
{
#ifdef _GLES_
	vertFilename += ".es";
	fragFilename += ".es";
#endif
	// Find path to shader
	std::string vertPath = WolfEngine::FindAssetFolder() + "Shaders/" + vertFilename;
	std::string fragPath = WolfEngine::FindAssetFolder() + "Shaders/" + fragFilename;

	GLuint vertexShaderID = glCreateShader(GL_VERTEX_SHADER);
	GLuint fragmentShaderID = glCreateShader(GL_FRAGMENT_SHADER);

	// Load vertex code
	std::ifstream vertFile(vertPath);
	if(!vertFile.is_open())
	{
		printf("Could not open file %s\n", vertPath.c_str());
		return;
	}
	vertFile.seekg(0, std::ios::end);
	size_t size = vertFile.tellg();
	std::string vertShaderCode(size, ' ');
	vertFile.seekg(0);
	vertFile.read(&vertShaderCode[0], size);

	// Load fragment code
	std::ifstream fragFile(fragPath);
	if(!fragFile.is_open())
	{
		printf("Could not open file %s\n", fragPath.c_str());
		return;
	}
	fragFile.seekg(0, std::ios::end);
	size = fragFile.tellg();
	std::string fragShaderCode(size, ' ');
	fragFile.seekg(0);
	fragFile.read(&fragShaderCode[0], size);

	GLint result = GL_FALSE;
	int infoLogLength;

	// Compile vertex shader
	printf("Compiling shader : %s\n", vertPath.c_str());
	char const * vertexSourcePointer = vertShaderCode.c_str();
	glShaderSource(vertexShaderID, 1, &vertexSourcePointer , NULL);
	glCompileShader(vertexShaderID);

	// Error check vertex Shader
	glGetShaderiv(vertexShaderID, GL_COMPILE_STATUS, &result);
	glGetShaderiv(vertexShaderID, GL_INFO_LOG_LENGTH, &infoLogLength);
	if (infoLogLength > 0)
	{
		std::vector<char> errorMessage(infoLogLength+1);
		glGetShaderInfoLog(vertexShaderID, infoLogLength, NULL, &errorMessage[0]);
		printf("%s\n", &errorMessage[0]);
	}

	// Compile fragment Shader
	printf("Compiling shader : %s\n", fragPath.c_str());
	char const * fragmentSourcePointer = fragShaderCode.c_str();
	glShaderSource(fragmentShaderID, 1, &fragmentSourcePointer , NULL);
	glCompileShader(fragmentShaderID);

	// Error check fragment Shader
	glGetShaderiv(fragmentShaderID, GL_COMPILE_STATUS, &result);
	glGetShaderiv(fragmentShaderID, GL_INFO_LOG_LENGTH, &infoLogLength);
	if (infoLogLength > 0)
	{
		std::vector<char> errorMessage(infoLogLength+1);
		glGetShaderInfoLog(fragmentShaderID, infoLogLength, NULL, &errorMessage[0]);
		printf("%s\n", &errorMessage[0]);
	}

	// Link the shader program
	id = glCreateProgram();
	glAttachShader(id, vertexShaderID);
	glAttachShader(id, fragmentShaderID);
	glLinkProgram(id);

	// Error check the program
	glGetProgramiv(id, GL_LINK_STATUS, &result);
	glGetProgramiv(id, GL_INFO_LOG_LENGTH, &infoLogLength);
	if ( infoLogLength > 0 )
	{
		std::vector<char> errorMessage(infoLogLength+1);
		glGetProgramInfoLog(id, infoLogLength, NULL, &errorMessage[0]);
		printf("%s\n", &errorMessage[0]);
	}

	// Clean up
	glDetachShader(id, vertexShaderID);
	glDetachShader(id, fragmentShaderID);
	glDeleteShader(vertexShaderID);
	glDeleteShader(fragmentShaderID);
}

Shader::~Shader()
{
	glDeleteProgram(id);
}