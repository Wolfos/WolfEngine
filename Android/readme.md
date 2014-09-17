====Android build====

Just get the zip file from http://wolfengine.net/android-deps.zip and extract it in the jni folder, this file contains the sources for LibPNG and the SDL libraries you need to build WolfEngine.
Then you can run make.bat from the main WolfEngine folder, which automatically copies any assets, builds an APK and pushes to device.

You may have to manually edit Android.mk to include any new .cpp files that have been added, since this port is not maintained very actively and doesn't use CMake.