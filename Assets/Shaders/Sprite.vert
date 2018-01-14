#version 330 core
layout(location = 0) in vec3 vertexPosition_modelspace;
uniform mat4 pMatrix, vMatrix, mMatrix;
void main(){
    gl_Position =  pMatrix*vMatrix*mMatrix * vec4(vertexPosition_modelspace,1);
}