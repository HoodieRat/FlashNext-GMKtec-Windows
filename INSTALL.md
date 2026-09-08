# Installing FlashNext

## The easy method

1. Confirm this is a GMKtec EVO-X2 with Ryzen AI Max+ 395, Radeon 8060S, 128 GB RAM, and Windows 11 x64.
2. Set the BIOS UMA frame buffer to **96 GB**, save, and reboot.
3. Make sure C: has 40 GiB free and the intended model drive has at least 115 GiB free (140 GiB recommended).
4. Download the repository ZIP from GitHub and extract it to a normal local folder such as `C:\FlashNext`.
5. Double-click `install.cmd`.
6. Approve the Windows installation/deployment prompts.
7. Accept or change the suggested model folder.
8. Type `ACCEPT` when asked to accept the Qwen Community License.
9. Leave the installer open until it reports success. The first run installs development tools, compiles the Vulkan runtime, downloads about 107 GiB of model data, verifies it, and runs a real inference check.
10. Open **FlashNext** from the Start menu, select **Start AI server**, and wait for **Running**.

The installer is resumable. If a download is interrupted or Windows requests a reboot, rerun `install.cmd`; verified work is reused.

## Optional command-line installation

Open Command Prompt in the extracted project folder:

```bat
install.cmd -ModelDirectory "D:\FlashNextModels\Qwen3.8-Flash-Next-UD-Q4_K_XL"
```

Useful options:

- `-ModelDirectory "D:\path"` chooses the model location in advance.
- `-AcceptLicense` records non-interactive acceptance of the Qwen Community License. Use it only after reading that license.
- `-NoLaunch` finishes without opening FlashNext.
- `-ForceDependencyRepair` rechecks and repairs required build tools.
- `-RestartDashboard` lets an application-only reinstall close and reopen the dashboard safely.

`-SkipModel` is intended for development or staged setup; the application cannot perform inference until the model is installed.

## If installation stops

- Read the final error shown in the installer window.
- Run `diagnose-install.cmd` to create a diagnostic report under `%LOCALAPPDATA%\FlashNextManager\diagnostics`.
- Correct the reported problem and run `install.cmd` again.
- If the Vulkan check reports less than 90 GiB of device-local memory, set BIOS UMA to 96 GB and reboot.
- If disk space is low, free space or choose another model drive.

For installer internals and recovery behavior, see [docs/INSTALLATION.md](docs/INSTALLATION.md).
