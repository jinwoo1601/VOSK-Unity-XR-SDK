# Project Instructions

## Android Toolchain Paths

All tools are inside the Unity 6000.3.7f1 installation:

- **NDK**: `C:\Program Files\Unity\Hub\Editor\6000.3.7f1\Editor\Data\PlaybackEngines\AndroidPlayer\NDK`
- **Platform Tools (adb)**: `C:\Program Files\Unity\Hub\Editor\6000.3.7f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools`
- **CMake**: `C:\Program Files\Unity\Hub\Editor\6000.3.7f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\cmake\3.22.1\bin\cmake.exe`
- **Ninja**: `C:\Program Files\Unity\Hub\Editor\6000.3.7f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\cmake\3.22.1\bin\ninja.exe`

### WSL Usage

From WSL, prefix paths with `/mnt/c/...` and call `.exe` variants directly:

```bash
# adb
"/mnt/c/Program Files/Unity/Hub/Editor/6000.3.7f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"

# logcat (filtered for vosk-bridge)
"/mnt/c/Program Files/Unity/Hub/Editor/6000.3.7f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe" logcat -s "vosk-bridge:*" "Unity:*"

# cmake (use Windows-style paths for -B, -S, toolchain since it's a .exe)
"/mnt/c/Program Files/Unity/Hub/Editor/6000.3.7f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/cmake/3.22.1/bin/cmake.exe"
```

### Building NativeBridge

```bash
CMAKE="/mnt/c/Program Files/Unity/Hub/Editor/6000.3.7f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/cmake/3.22.1/bin/cmake.exe"
NDK_WIN="C:/Program Files/Unity/Hub/Editor/6000.3.7f1/Editor/Data/PlaybackEngines/AndroidPlayer/NDK"

"$CMAKE" -B "D:/Game Development/VOSK-Unity-XR-SDK/NativeBridge~/build" \
         -S "D:/Game Development/VOSK-Unity-XR-SDK/NativeBridge~" \
         -DCMAKE_TOOLCHAIN_FILE="$NDK_WIN/build/cmake/android.toolchain.cmake" \
         -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-27 -DANDROID_STL=c++_shared \
         -DCMAKE_BUILD_TYPE=Release \
         -DCMAKE_MAKE_PROGRAM="C:/Program Files/Unity/Hub/Editor/6000.3.7f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/cmake/3.22.1/bin/ninja.exe" \
         -G Ninja

"$CMAKE" --build "D:/Game Development/VOSK-Unity-XR-SDK/NativeBridge~/build" --config Release -j 4
```
