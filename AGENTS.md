# Repository Guidelines

## Mandatory: install updates immediately

Ian requires every session working on FlashNext to **install application updates immediately so he can test them**. Source edits, successful compilation, and a staged/published folder are not completion. Do not leave deployment as a manual step for Ian.

For changes affecting the application:

1. Build/publish the updated Dashboard and Manager together, retaining the shared Core and Infrastructure libraries and required support files.
2. Stop generation normally if necessary, preserve the conversation/draft, stop the managed server, and fully quit the dashboard. Closing its window only hides it; use the tray's Quit command. Necessary stop/close/relaunch is part of the update workflow unless the user explicitly prohibits it for that task.
3. Install into the active current-user application using the existing inventory-verified updater, retaining its rollback copy. Do not modify model weights, runtime pins, secrets, or user settings for an app-only update.
4. Verify installed file hashes against the published build, verify the normal launcher/shortcut target, reopen the installed dashboard, and check the changed behavior. Do not rely on the assembly version alone.
5. Report completion only after installation and verification. If a later explicit do-not-stop instruction or another blocker prevents installation, surface that conflict and request direction immediately; never present staging as a finished update.

## Project and deployment context

Core owns report models/capture/formatting; Dashboard is the WPF chat/tray application; Manager is the console host. Both use Infrastructure.Windows for runtime supervision and Windows integration.

The active app is `%LOCALAPPDATA%\FlashNextManager\app\current`; `run.cmd` prefers it over Program Files. Conversations and settings live outside that app directory.

## Build and verification

Use `dotnet publish src\FlashNext.Dashboard\FlashNext.Dashboard.csproj -c Release` and the corresponding Manager project. `scripts\Update-InstalledApp.ps1 -PublishRoot <folder>` prepares the inventory; apply with `-Apply -InventoryHash <hash> -ResultPath <file>`.

Honor restrictions on tests, inference, and benchmarks. `scripts\Publish-AppUpdate.ps1` runs tests: do not use it when tests are prohibited. Compile/publish and use the inventory updater instead.
