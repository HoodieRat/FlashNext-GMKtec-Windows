# FlashNext-GMKtec-Windows 1.0.2

## Installer failure corrected

The previous installer launched nearly the entire installation pipeline inside one elevated PowerShell process. When that child failed, the parent retained only the exit code and directed the user to the normal-user log even though the detailed child log was under ProgramData. The resulting message, `Elevated installation stage failed with exit code 1`, did not identify the failing command.

The old dependency stage also issued `winget source update --disable-interactivity --accept-source-agreements`. Current WinGet exposes `--accept-source-agreements` for package operations such as `list` and `install`, but not for the `source update` or `source list` subcommands. That invalid subcommand option created an immediate exit-code-1 path before any dependency could be installed. Revision 1.0.2 uses `winget source list --disable-interactivity` and updates only the required source with `winget source update --name winget --disable-interactivity`.

Revision 1.0.2 replaces the old execution and error-reporting design.

## New execution model

- WinGet is resolved and validated before any FlashNext UAC handoff.
- Only the official `winget` source is updated; unrelated configured sources are untouched.
- Dependency acquisition runs from the normal user session. Individual signed installers request approval directly when needed.
- Python environment creation, PyPI verification, .NET restore, tests, self-contained publishing, Git checkout, CMake configuration, and Vulkan runtime compilation run without an elevated PowerShell process.
- The FlashNext administrator helper performs only long-path enablement and verified Program Files deployment.

## Exact failure reporting

Dependency, build, and deployment phases now write separate structured JSON results under `%LOCALAPPDATA%\FlashNextManager\state`. Each failed result records the exact phase, exception message, stack trace when available, and relevant log path. Native command exceptions include the captured output tail.

The elevated handoff uses an encoded PowerShell command and a serialized parameter table rather than a fragile nested command-line quoting chain. The parent requires a result file and reports its stage/message. A startup or parameter-binding failure that occurs before result creation is explicitly identified as such.

`diagnose-install.cmd` now collects:

- Microsoft Desktop App Installer and WinGet status;
- dependency, build, deployment, and install-state records;
- normal installer, dependency, manager build/test, Vulkan runtime build, and administrator deployment log tails;
- the earlier `machine-install` administrator log when a prior package produced one.

## Transactional deployment

The normal-user build stage creates complete recursive file inventories containing relative path, byte count, and SHA-256 for both manager and runtime.

The administrator helper:

1. verifies the successful build report and revision;
2. rejects reparse points in the staging tree;
3. verifies every staged file;
4. copies to unique incoming Program Files directories;
5. verifies every copied file again;
6. moves the existing manager/runtime to `previous`;
7. activates the new manager/runtime together;
8. restores both prior directories if activation or final verification fails.

No network request, package operation, source build, .NET operation, test, or Python operation runs in that administrator helper.

## Python environment location

The pinned Python environment is now stored at `%LOCALAPPDATA%\FlashNextManager\python-env` rather than under Program Files. The C# model manager resolves that same location, so model download/repair no longer depends on writing Python package state into a machine-owned directory.

## Additional corrections

- Installation stages expanded to separate dependencies, build, and machine deployment for cleaner resumption.
- A failed dependency repair overwrites an older successful result instead of accidentally preserving stale success state.
- `hf_xet` is pinned to the current verified 1.6.0 release with a Windows x64 ABI3 wheel available for Python 3.13.
- Project assembly/file version advanced to 1.0.2.
- Installer architecture tests now reject WinGet, pip, dotnet, Git/CMake build operations, or runtime compilation inside the administrator deployment helper.
