# Installation internals

The installer is a resumable Windows PowerShell 5.1 state machine. Revision 1.0.5 verifies the bootstrap Vulkan probe hash, then measures the Radeon 8060S device-local heap with a direct Vulkan memory probe before installing or repairing dependencies. A 1.0.4 install state is migrated in place. User-context work remains separate from machine deployment so an administrator process is never responsible for WinGet source access, package restore, tests, publishing, or Vulkan compilation.

## Durable stages

1. `new`: no checks completed for the current project revision.
2. `preflight-complete`: Windows 11 x64, normalized target CPU/GPU identity, and SMBIOS-backed installed-memory capacity passed. OS-visible memory is recorded separately.
3. `dependencies-complete`: the locked WinGet packages were detected or installed from the normal user session and recorded in `state\dependency-install-result.json`.
4. `build-complete`: the Python environment, .NET restore/tests/publish, and pinned Vulkan runtime build completed in the normal user session. The result and complete SHA-256 inventories are in `state\build-install-result.json`.
5. `machine-complete`: a deployment-only administrator process verified and atomically activated the staged manager/runtime under Program Files. Its result is in `state\machine-install-result.json`.
6. `hardware-complete`: the installed manager successfully enumerated Vulkan and confirmed the required device-local heap.
7. `model-complete`: the four target shards, MTP sidecar, and vision projector passed byte-count, SHA-256, and GGUF checks and completed the live smoke test.
8. `complete`: shortcut creation and final installation state completed.

State is written atomically after each successful phase. A failed phase does not advance the state. Rerunning `install.cmd` reuses valid reports and build caches where possible.

## Phase 1: hardware and Windows preflight

The initial check runs without elevation. It confirms Windows 11 x64, Ryzen AI Max+ 395, Radeon 8060S, and at least 120 GiB physically installed memory. Windows GPU display names are normalized, so `AMD Radeon(TM) 8060S Graphics` is accepted. Installed RAM is read with `GetPhysicallyInstalledSystemMemory`, with `Win32_PhysicalMemory.Capacity` as fallback. `Win32_ComputerSystem.TotalPhysicalMemory` is retained only as the OS-visible figure after firmware/UMA reservations.

The installer does not infer the BIOS UMA setting from ordinary Windows RAM values, `Win32_VideoController.AdapterRAM`, or a full `vulkaninfo` dump. The first heap gate loads the Windows Vulkan loader, creates a minimal instance, enumerates physical devices, and reads `VkPhysicalDeviceMemoryProperties`. A largest target-device DEVICE_LOCAL heap of at least 90 GiB passes. A 64 GB UMA configuration can pass physical-memory detection but must fail that probe with:

`Set the GMKtec EVO-X2 BIOS UMA frame buffer to 96 GB, save the BIOS setting, reboot Windows, and rerun install.cmd.`

The installer never changes BIOS or firmware.

## Phase 2: dependency acquisition

`Install-Dependencies.ps1` runs in the invoking user's PowerShell process. It resolves the real Microsoft Desktop App Installer `winget.exe`, verifies that the official `winget` source is registered, and updates only that source. WinGet is used to acquire the pinned packages, but its human-formatted `list` table is diagnostic only and never determines success.

Before and after acquisition, `Dependency-Probes.ps1` verifies the installed capabilities directly. Git and CMake must start and report compatible normalized versions; Python must be 64-bit 3.13; `dotnet --list-sdks` must contain a compatible .NET 10 SDK; `vswhere`, `cl.exe`, `vcvars64.bat`, and a complete Windows SDK must be present; and the selected Vulkan SDK must contain `glslc.exe`, `vulkan.h`, and `vulkan-1.lib`. Git for Windows output such as `2.55.0.windows.3` is normalized to `2.55.0.3`. The verified executable and SDK paths are written to the dependency report and passed into the build stage instead of being rediscovered through WinGet output.

Individual signed installers can display their own Windows approval prompt. FlashNext does not wrap WinGet itself in one large elevated process. A reboot-required package records its package/stage and instructs the user to reboot and rerun `install.cmd`. If a normal install or upgrade returns an unusual WinGet code but the direct capability probe passes, the verified tool is accepted and the code is retained as a warning. If the probe fails, the installer makes at most one force-repair attempt and then reports both the direct evidence and WinGet output tail.

The locked packages are Git, CMake, Python 3.13 x64, .NET SDK 10, Visual Studio 2022 Build Tools with the VCTools workload and Windows SDK, and the Vulkan SDK. Python package wheels are downloaded into a local wheelhouse and verified against the official PyPI SHA-256 metadata before offline installation.

## Phase 3: build and test as the current user

`Build-FlashNext.ps1` performs all source and build work without an elevated PowerShell token:

- validates Python 3.13, .NET SDK 10, Git, CMake, MSVC, Windows SDK, Vulkan SDK, and `glslc`;
- creates `%LOCALAPPDATA%\FlashNextManager\python-env`;
- downloads and verifies the pinned Hugging Face client/Xet packages;
- restores the solution and runs all non-hardware tests;
- publishes a self-contained `win-x64` manager;
- checks out the exact detached llama.cpp runtime commit;
- configures and compiles the Vulkan server, CLI, and benchmark targets;
- verifies runtime version, Vulkan device enumeration, and required server flags;
- writes recursive byte-count/SHA-256 inventories for the staged manager and runtime.

The runtime source and CMake cache remain under `%LOCALAPPDATA%\FlashNextManager\build-cache`, so a later rerun can reuse completed Git and compilation work.

## Phase 4: verified Program Files deployment

Only `Install-MachineComponents.ps1` is launched through the FlashNext UAC handoff. It performs no network access, package installation, Python operation, .NET operation, test, or runtime build.

The helper validates the build result and inventories, rejects reparse points in the staging tree, checks that the installed manager/server are not running, enables Windows long-path support, and copies the artifacts into unique incoming directories under `%ProgramFiles%\FlashNextManager`. It verifies every copied file again before activation.

Manager and runtime are switched together. Existing `current` directories become `previous`. If either activation or post-copy verification fails, the helper removes the partial new release and restores both prior directories. The administrator result is written to the current user's state directory and the administrator log is mirrored into the normal installer log.

## Model acquisition

The downloader rejects custom Hugging Face endpoints. It uses the immutable model revision, exact six-file allowlist, official Hugging Face client, no more than two concurrent large transfers, resumable cache data, and same-volume staging. Active files are promoted only after the complete set passes exact byte and SHA-256 verification plus GGUF header checks. Invalid files are quarantined before reacquisition.

## Failure reporting

The parent installer no longer reduces an administrator failure to a bare exit code. Dependency, build, and deployment phases each write a structured result containing status, stage, message, stack trace when available, and log path. Native command failures include the captured output tail.

Run `diagnose-install.cmd` after any failure. It collects:

- WinGet/App Installer diagnostics;
- dependency, build, and deployment result files;
- install state;
- normal installer, dependency, build, runtime-build, and deployment log tails;
- an earlier `machine-install` log from pre-1.0.2 attempts when one exists.
