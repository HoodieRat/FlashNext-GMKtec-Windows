# Prebuilt application update

The release source-installer ZIP contains `install.cmd` and the pinned bootstrap probe for first-time provisioning or source-based reinstallation. The separate prebuilt app-update ZIP contains the rebuilt Dashboard and Manager together, shared libraries, support files, `app.inventory.json`, and this existing inventory updater. It does not include an inference runtime or model weights. Use it only on an already provisioned FlashNext machine with the matching runtime/model pins.

Application updates preserve user settings, conversations, API keys, model files, and the installed inference runtime. They retain the previous application directory as a rollback copy. Download only from this repository's release and compare the archive SHA-256 with its `SHA256SUMS.txt` before extraction. The bundle's inventory hash is also published in the release notes; this is an integrity check, not a code signature.

1. Extract the app-update ZIP to a local folder.
2. Finish or normally stop any generation. Preserve unsent text. Stop the managed server and choose **Quit FlashNext** from the tray menu; closing the window only hides it. Exit the console Manager too if it is running.
3. Open PowerShell in the extracted folder. Substitute the inventory hash published in the release notes, not a hash calculated from an untrusted modified inventory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\app\scripts\Update-InstalledApp.ps1 -PublishRoot "$PWD\app" -Apply -InventoryHash '<published inventory SHA-256>' -ResultPath "$PWD\update-result.json"
```

4. Require exit code 0 and `status: succeeded` in `update-result.json`. The updater verifies every file against the inventory before and after activation. The result identifies the previous application's rollback folder.
5. Open the normal FlashNext shortcut (it should target `%LOCALAPPDATA%\FlashNextManager\app\current\FlashNext.Dashboard.exe`), or run that installed executable directly. Start the AI server and confirm Running. If your old shortcut points elsewhere, use the source installer's shortcut setup instead of launching an outdated copy.

The prebuilt update is self-contained for .NET and does not require compiling the app. It does not repair a missing runtime/model installation. Do not replace the runtime or reset saved settings to apply an app-only update.
