/*
WolfEngine � 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "SpriteRenderer.h"
#include "../ECS/GameObject.h"
#include "../WolfEngine.h"

Shader* SpriteRenderer::defaultShader = NULL;

void SpriteRenderer::Added()
{
	center = new SDL_Point;
	mesh = new Mesh();
	mesh->CreateQuad();

	if(defaultShader == NULL) defaultShader = new Shader( "Sprite.vert", "Sprite.frag" );
	shader = defaultShader;
}

void SpriteRenderer::Update()
{
	if (layer >= WolfEngine::scene->layers)
	{
		WolfEngine::scene->layers = layer + 1;
	}
}

void SpriteRenderer::Render(Camera* camera)
{
	// Set shader
	glUseProgram(shader->id);

	Matrix model = gameObject->transform->GetMatrix();
	Matrix view = camera->gameObject->transform->GetMatrix();
	Matrix projection = camera->GetProjection();

	Matrix mvp = model * view * projection;

	glUniformMatrix4fv(glGetUniformLocation(shader->id, "mvp"), 1, GL_FALSE, &mvp.GetData()[0]);

	glActiveTexture(GL_TEXTURE0);
	glBindTexture(GL_TEXTURE_2D, spriteSheet->textureID);
	glUniform1i(glGetUniformLocation(shader->id,  "sampler"), 0);

	// Vertices
	glEnableVertexAttribArray(0);
	glBindBuffer(GL_ARRAY_BUFFER, mesh->vertexBuffer);
	glVertexAttribPointer(0, 3, GL_FLOAT, false, 0, (void*)0);

	// UVs
	glEnableVertexAttribArray(1);
	glBindBuffer(GL_ARRAY_BUFFER, mesh->uvBuffer);
	glVertexAttribPointer(1, 2, GL_FLOAT, false, 0, (void*)0);

	glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, mesh->indexBuffer);

	// Draw
	glDrawElements(GL_TRIANGLES, mesh->indices.size(), GL_UNSIGNED_INT, (void*)0);
	glDisableVertexAttribArray(0);
	glDisableVertexAttribArray(1);
}


void SpriteRenderer::Load(std::string filename)
{
	spriteSheet = new Bitmap(filename);
    frameWidth = spriteSheet->size.x;
    frameHeight = spriteSheet->size.y;
}
