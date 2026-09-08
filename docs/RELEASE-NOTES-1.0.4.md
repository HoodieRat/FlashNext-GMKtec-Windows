# FlashNext-GMKtec-Windows 1.0.4

## Corrected Windows Vulkan SDK verification

The 1.0.3 dependency probe incorrectly required `C:\VulkanSDK\<version>\Bin\vulkaninfo.exe`. LunarG's official Windows SDK deliberately names its bundled binary `vulkaninfoSDK.exe` so it is not confused with the `vulkaninfo.exe` that an independent hardware vendor may distribute with the Vulkan runtime. This caused a complete SDK installation to be rejected even though `glslc.exe`, `Include\vulkan\vulkan.h`, and `Lib\vulkan-1.lib` were all present.

Revision 1.0.4:

- treats `glslc.exe`, the Vulkan headers, and `vulkan-1.lib` as the required SDK build capability;
- resolves `vulkaninfoSDK.exe` first and accepts `vulkaninfo.exe` as the driver/runtime fallback;
- searches the exact SDK root, all `VULKAN_SDK` scopes, conventional `C:\VulkanSDK` versions, and `PATH`;
- records the executable actually used, both exit codes, parsed devices, and memory heaps;
- accepts a successfully parsed Radeon 8060S and `DEVICE_LOCAL` heap even if the information utility returns a nonzero code for an unrelated optional query, while preserving a warning and output tail;
- uses the same dual-name resolution in the post-deployment .NET hardware probe.

## Visual Studio 18 build compatibility

The target machine reported a valid Visual Studio 18 C++ installation, but the runtime build script was hard-coded to `Visual Studio 17 2022`. Revision 1.0.4 reads the verified Visual Studio installation/version, inspects the generators supported by the installed CMake, selects `Visual Studio 18 2026` or `Visual Studio 17 2022` as appropriate, pins the selected instance through `CMAKE_GENERATOR_INSTANCE`, and removes an incompatible CMake cache before rebuilding.

## Resume behavior

Existing compatible Git, CMake, Python, .NET, Visual C++, Windows SDK, and Vulkan SDK installations are probed directly and skipped. The project revision change invalidates failed 1.0.3 stage reports but does not uninstall working dependencies.

The factory model still requires an actual Vulkan `DEVICE_LOCAL` heap of at least 90 GiB. On the 128-GiB EVO-X2, set BIOS UMA to 96 GB and reboot before the build/model stages can pass.
