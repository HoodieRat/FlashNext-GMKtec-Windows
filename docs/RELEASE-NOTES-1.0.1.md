# FlashNext 1.0.1 hardware-preflight correction

This revision corrects two blocking first-run defects:

1. Windows may identify the target GPU as `AMD Radeon(TM) 8060S Graphics`. CPU and GPU checks now normalize punctuation, trademark text, spacing, and letter case before matching required hardware tokens. The PowerShell installer and the .NET manager use the same rule.
2. `Win32_ComputerSystem.TotalPhysicalMemory` is the memory exposed to Windows after firmware reservations. On an EVO-X2 with a large UMA frame buffer, it can be much lower than the 128 GB physically installed capacity. The installer now reads installed capacity from `GetPhysicallyInstalledSystemMemory`, falls back to the sum of `Win32_PhysicalMemory.Capacity`, and records OS-visible memory separately.

The Vulkan stage still requires at least a 90 GiB device-local heap. This keeps the intended 96 GB BIOS UMA requirement while allowing the basic 128 GB physical-memory check to work correctly.

The install-state file now records project revision `1.0.1` and hardware-preflight version `2`. Rerunning `install.cmd` safely rechecks hardware and republishes an older installed manager/runtime when necessary.
