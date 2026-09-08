# FlashNext-GMKtec-Windows 1.0.5

Revision 1.0.5 repairs the installer hang on the full `vulkaninfoSDK.exe` report and completes the factory Windows Vulkan installation path.

## Blocking failure repaired

1.0.4 ran `vulkaninfoSDK.exe --summary` (fast) and then a no-argument `vulkaninfoSDK.exe` (unbounded). The second process could enumerate formats, queues, presentation, and video features for minutes with only a blinking caret. `Invoke-FlashNextNative` used `WaitForExit()` with no timeout.

1.0.5 replaces that path with `FlashNext.VulkanProbe.exe`, a Native AOT console application that:

- loads the Windows Vulkan loader (`vulkan-1.dll`)
- creates a minimal instance with no presentation or surface extensions
- enumerates physical devices
- reads `VkPhysicalDeviceProperties` and `VkPhysicalDeviceMemoryProperties`
- reports DEVICE_LOCAL heap byte sizes as one UTF-8 JSON document
- exits in a few seconds under normal conditions

A largest Radeon 8060S DEVICE_LOCAL heap of at least 90 GiB passes. A smaller heap stops before dependency installation, compilation, or model download, with this instruction:

`Set the GMKtec EVO-X2 BIOS UMA frame buffer to 96 GB, save the BIOS setting, reboot Windows, and rerun install.cmd.`

The installer never changes BIOS or firmware.

## Process execution

Every native process now has an explicit timeout, PID logging, elapsed-time heartbeat, stdout/stderr capture, secret redaction, Job Object plus `taskkill /T` process-tree termination, and a timeout exception that preserves tails and recovery text. Ctrl+C does not leave a false success state.

## State migration

Existing 1.0.4 `install-state.json` files are backed up and migrated in place. Verified dependencies and model files are reused. The obsolete vulkaninfo full-report hardware result is discarded and the direct probe runs again. The manager and probe are rebuilt and redeployed. The user does not delete state, uninstall dependencies, or extract over an old source folder.

## Bootstrap binary

`bootstrap/FlashNext.VulkanProbe.exe` is hashed in `manifests/bootstrap.lock.json`. `install.cmd` verifies that hash before the first heap measurement, so the preflight does not require a separately installed .NET runtime. The normal build stage rebuilds the probe from source and deploys the rebuilt binary with the manager.
