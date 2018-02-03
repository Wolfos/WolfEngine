//
// Created by Robin on 28/01/2018.
//

#include "Mesh.h"


Mesh::Mesh()
{

}

Mesh::~Mesh()
{
	CleanBuffers();
	vertices.clear();
	uvs.clear();
	indices.clear();
}

void Mesh::Apply()
{
	CleanBuffers();
	glGenVertexArrays(1, &vArrayID);
	glBindVertexArray(vArrayID);

	glGenBuffers(1, &vertexBuffer);
	glBindBuffer(GL_ARRAY_BUFFER, vertexBuffer);
	glBufferData(GL_ARRAY_BUFFER, vertices.size() * sizeof(float) * 3, &vertices[0], GL_STATIC_DRAW);

	glGenBuffers(1, &uvBuffer);
	glBindBuffer(GL_ARRAY_BUFFER, uvBuffer);
	glBufferData(GL_ARRAY_BUFFER, uvs.size() * sizeof(float) * 2, &uvs[0], GL_STATIC_DRAW);

	glGenBuffers(1, &indexBuffer);
	glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, indexBuffer);
	glBufferData(GL_ELEMENT_ARRAY_BUFFER, indices.size() * sizeof(unsigned int), &indices[0], GL_STATIC_DRAW);

	hasBuffers = true;
}

void Mesh::ApplyUVs()
{
	if(hasBuffers)
	{
		glDeleteBuffers(1, &uvBuffer);
	}

	glGenBuffers(1, &uvBuffer);
	glBindBuffer(GL_ARRAY_BUFFER, uvBuffer);
	glBufferData(GL_ARRAY_BUFFER, uvs.size() * sizeof(float) * 2, &uvs[0], GL_STATIC_DRAW);
}

void Mesh::CleanBuffers()
{
	if(hasBuffers)
	{
		glDeleteVertexArrays(1, &vArrayID);
		glDeleteBuffers(1, &vertexBuffer);
		glDeleteBuffers(1, &uvBuffer);
		glDeleteBuffers(1, &indexBuffer);
		hasBuffers = false;
	}
}

void Mesh::CreateQuad()
{
	vertices.clear();
	uvs.clear();
	indices.clear();

	vertices.push_back({-1.0f, -1.0f, 0.0f});
	vertices.push_back({1.0f, -1.0f, 0.0f});
	vertices.push_back({-1.0f, 1.0f, 0.0f});
	vertices.push_back({1.0f, 1.0f, 0.0f});

	uvs.push_back({0.0f, 0.0f});
	uvs.push_back({1.0f, 0.0f});
	uvs.push_back({0.0f, 1.0f});
	uvs.push_back({1.0f, 1.0f});

	indices.push_back(0);
	indices.push_back(1);
	indices.push_back(2);
	indices.push_back(3);
	indices.push_back(2);
	indices.push_back(1);

	Apply();
}