
# Uncomment this if you're using STL in your project
# See CPLUSPLUS-SUPPORT.html in the NDK documentation for more information
APP_STL := gnustl_shared

# use this to select gcc instead of clang
NDK_TOOLCHAIN_VERSION := 4.8



# then enable c++11 extentions in source code
APP_CPPFLAGS += -std=gnu++11 -frtti