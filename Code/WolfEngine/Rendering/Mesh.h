//
// Created by Robin on 28/01/2018.
//

#pragma once
#include <vector>
#include "../Math/Vectors.h"
#include "../Includes.h"

class Mesh
{
public:
	std::vector<Vector3<float>> vertices;
	std::vector<Vector2<float>> uvs;
	std::vector<unsigned int> indices;

	GLuint vArrayID, vertexBuffer, uvBuffer, indexBuffer;

	Mesh();
	~Mesh();

	/// Send the mesh data to the GPU
	void Apply();
	void ApplyUVs();
	/// The mesh becomes a quad
	void CreateQuad();

private:
	bool hasBuffers = false; // Whether we currently have buffers stored on the GPU
	void CleanBuffers(); // Free all buffers currently stored on the GPU
};