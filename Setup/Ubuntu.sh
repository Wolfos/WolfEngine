#!/bin/bash
sudo apt-get install gcc build-essential cmake -y
sudo apt-get install libsdl2-2.0-0 libsdl2-dbg libsdl2-dev libsdl2-mixer-2.0-0 libsdl2-mixer-dbg libsdl2-mixer-dev libsdl2-ttf-2.0-0 libsdl2-ttf-dbg libsdl2-ttf-dev -y
sudo apt-get install libXmu-dev libXi-dev libgl-dev dos2unix git wget -y
cd ..
mkdir build
cd build
cmake -g "Unix Makefiles" ..
