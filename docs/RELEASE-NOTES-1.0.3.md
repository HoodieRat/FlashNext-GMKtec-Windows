# FlashNext-GMKtec-Windows 1.0.3

## Dependency verification correction

Revision 1.0.2 installed Git successfully through WinGet and then attempted to confirm it by parsing the human-formatted `winget list` table. WinGet table output is not a stable machine interface: column widths, progress/control output, localization, and version presentation can vary. On Windows Package Manager 1.29.289, the parser returned no package version even though the Git installer completed, so FlashNext incorrectly stopped with “Git.Git could not be confirmed.”

Revision 1.0.3 removes that table parser from the success path for every dependency.

## Direct capability probes

The installer now proves that each dependency is usable:

- Git: finds `git.exe`, runs `git --version`, and normalizes Git for Windows versions such as `2.55.0.windows.3` to `2.55.0.3`.
- CMake: runs `cmake --version` and compares a normalized version.
- Python: starts Python 3.13, confirms the patch version, confirms a 64-bit process, and records the selected interpreter/launcher.
- .NET: reads `dotnet --list-sdks` and requires a compatible .NET 10 SDK.
- Visual Studio Build Tools: uses `vswhere`, requires the x64/x86 Visual C++ tools component, confirms `vcvars64.bat` and `cl.exe`, and confirms a complete Windows SDK containing headers, libraries, and `rc.exe`.
- Vulkan SDK: confirms the selected SDK version and requires `Bin\glslc.exe`, `Include\vulkan\vulkan.h`, and `Lib\vulkan-1.lib`.

The verified paths are persisted in `dependency-install-result.json`. The build receives the verified Vulkan SDK path explicitly rather than depending on a newly refreshed environment variable.

## Bounded repair and better evidence

A package that is missing or incompatible is installed or upgraded through the pinned official WinGet package. If the first operation finishes but the direct probe still fails, the installer makes one force-repair attempt. It never loops indefinitely. The final error includes the direct probe evidence, the WinGet diagnostic output, each attempted action, and each exit code.

A nonzero WinGet exit code no longer creates a false failure when the directly verified capability is present and compatible. The code is retained in the report as a warning.


## Earlier Vulkan/UMA validation

After dependencies are directly verified, the installer now runs the Vulkan information utility (1.0.3 incorrectly assumed the SDK file was named `vulkaninfo.exe`; corrected in 1.0.4) before compiling the runtime. It verifies that Vulkan enumerates the Radeon 8060S and that the largest `DEVICE_LOCAL` heap is at least 90 GiB. A 128-GiB machine that currently exposes roughly 64 GiB to Windows receives an early warning because that pattern is consistent with a 64 GB UMA allocation; only the Vulkan heap result decides pass or fail. An insufficient result gives the exact instruction to set the EVO-X2 BIOS UMA frame buffer to 96 GB and rerun `install.cmd`.

## Resume behavior

Project revision is now `1.0.3`. Existing 1.0.1 or 1.0.2 state is revalidated. Git that was installed during the failed 1.0.2 attempt is detected directly and skipped; it is not needlessly reinstalled.

## Additional changes

- `global.json` now permits a later compatible .NET 10 feature band while retaining .NET 10 as the required major SDK.
- The dependency manifest documents a preferred package version, minimum compatible version, and verification method.
- Installation-contract tests reject reintroduction of the WinGet table parser and verify that the Vulkan SDK path is carried into the runtime build.
- Assembly and file version advanced to 1.0.3.
