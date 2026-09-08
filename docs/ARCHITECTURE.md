# Architecture

## Application boundaries

`FlashNext.Core` contains platform-neutral models, validation, atomic persistence, API streaming, Prometheus parsing, response metrics, conversation persistence, server argument construction, and service contracts. `FlashNext.VulkanProbe` is a Native AOT console application that P/Invokes the Windows Vulkan loader and writes one JSON document of physical devices and DEVICE_LOCAL heaps. `FlashNext.Infrastructure.Windows` implements Windows process execution with mandatory timeouts and Job Object process-tree termination, current-user security ACLs, WMI plus direct-Vulkan hardware probing, runtime/model lifecycle, diagnostics, telemetry, and explicit firewall orchestration. `FlashNext.Manager` supplies the Spectre.Console application, command-line installation modes, chat, benchmarking, settings, diagnostics, and lifecycle menus.

The manager is an orchestrator, not a second inference server. The pinned `llama-server.exe` remains the only API server. All consumers use the same authenticated OpenAI-compatible endpoint.

## Installer trust boundaries

The installation pipeline has two security contexts.

### Normal user context

The normal user process performs hardware identity checks, WinGet source validation, dependency acquisition, Python environment creation, PyPI wheel verification, .NET restore/tests/publish, Git checkout, CMake configuration, Vulkan runtime compilation, runtime feature checks, and recursive artifact inventory creation.

The staged release is located under `%LOCALAPPDATA%\FlashNextManager\install-staging`. Its result file includes complete relative paths, byte counts, and SHA-256 values for every manager/runtime file.

### Administrator deployment context

The elevated process receives only the application root, staging root, build-result path, result path, and parent log path. It does not receive or invoke WinGet, Python, pip, dotnet, Git, CMake, or the runtime build script.

It rejects reparse points, verifies the staging inventory, copies into unique incoming Program Files directories, verifies the copy, and activates manager/runtime as one transaction. If activation or post-copy validation fails, both previous directories are restored. A structured result is written before the elevated process exits.

## Durable state

User state is kept below `%LOCALAPPDATA%\FlashNextManager`, including the Python environment, installer results, logs, wheelhouse, build cache, settings, metrics, and conversations. Administrator deployment evidence is kept below `%ProgramData%\FlashNextManager`. Application and runtime assets are versioned under `%ProgramFiles%\FlashNextManager`. Model files are stored on a user-selected drive.

The durable installer stages are `new`, `preflight-complete`, `dependencies-complete`, `build-complete`, `machine-complete`, `hardware-complete`, `model-complete`, and `complete`.

## Lifecycle invariants

- One manager process and one manager-owned server process per user.
- A Windows Job Object terminates the owned server when the manager exits unexpectedly.
- A healthy external server can be attached and is never killed by this manager instance.
- Server arguments are passed as discrete process arguments, not concatenated through a shell.
- API security is provided through a protected key file and never through a printed command line.
- Installer, runtime, model, settings, and security writes use staging plus atomic activation or replacement.
- Previous runtime/application slots remain available until the successor is verified.
- Installer failure results identify the exact phase and log rather than relying on an undifferentiated process exit code.

## Inference data flow

1. The manager loads validated settings and protected API security material.
2. Hardware preflight reads WMI system identity plus the direct Vulkan memory probe JSON for device-local heap data.
3. Runtime supervision verifies server help options and builds the exact argument vector.
4. `llama-server` loads target shards plus the MTP sidecar on Vulkan.
5. The manager streams `/v1/chat/completions`, while metrics snapshots are read before and after the request.
6. Completion usage/timings, Prometheus deltas, process memory, and available system RAM are combined into a response record.
7. Content-free JSONL/CSV metrics are retained according to settings.

## Runtime update flow

Runtime updates build into `runtime/staging`. Binary checks are followed by a full model-load smoke test on a temporary port. Health, model listing, completion, metrics, and positive MTP deltas are mandatory. Only then does staging become current. The old current slot becomes previous. Failure leaves current untouched.
