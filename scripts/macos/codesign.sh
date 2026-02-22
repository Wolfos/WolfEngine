codesign --force --sign - --entitlements /tmp/wolfengine.instruments.entitlements /Users/robinvanee/WolfEngine/WolfEngine.Editor/bin/Debug/net8.0/WolfEngine.Editor
codesign -d --entitlements :- /Users/robinvanee/WolfEngine/WolfEngine.Editor/bin/Debug/net8.0/WolfEngine.Editor
