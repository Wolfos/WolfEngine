//
// Created by Robin on 01/01/2018.
//

#include "Random.h"
#include <random>

float WolfEngine::RandomRange(float min, float max)
{
	std::random_device rd;
	std::mt19937 eng(rd());
	std::uniform_real_distribution<float> distribution(min, max);
	return distribution(eng);
}

double WolfEngine::RandomRange(double min, double max)
{
	std::random_device rd;
	std::mt19937 eng(rd());
	std::uniform_real_distribution<double> distribution(min, max);
	return distribution(eng);
}

int WolfEngine::RandomRange(int min, int max)
{
	std::random_device rd;
	std::mt19937 eng(rd());
	std::uniform_int_distribution<int> distribution(min, max);
	return distribution(eng);
}