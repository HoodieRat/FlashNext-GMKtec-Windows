# Acceptance traceability

| Area | Implementation | Automated evidence | Hardware/Windows evidence |
|---|---|---|---|
| Complete build | Solution, four production projects, three test projects plus hang helper | `dotnet test FlashNext.sln -c Release` | Self-contained publish during install |
| No incomplete source markers | Repository contract scan and Python verifier | Installation test and `verify_repository.py` | Not applicable |
| Safe install/resume | Eight-stage user state machine; dependency/build/deployment result files; deployment-only UAC helper | Installation architecture tests and static verifier | Interrupted/reboot rerun on Windows |
| Dependency integrity | WinGet in user context, exact package versions, PyPI wheel hashes, `pip check`, environment lock | Manifest and installer-boundary validation | Dependency report and normal-user build report |
| Target hardware | WMI identity and direct Vulkan memory probe | Probe JSON evaluator tests plus process-timeout tests | EVO-X2 preflight with 96 GB UMA |
| Runtime pin | Detached exact commit, clean tree, Vulkan-only CMake in normal-user build stage | Manifest and script checks | Compile, device enumeration, help/version |
| Initial deployment/runtime activation | Recursive staged inventories, deployment copy verification, paired manager/runtime activation, rollback | Installation boundary and source-contract tests | Program Files deployment plus full model smoke test |
| Model integrity | Six-file allowlist, exact size/SHA, GGUF check, quarantine, atomic promotion | Manifest and Python syntax/logic validation | Full download and hash verification |
| Chat/API | Authenticated SSE client, multiline chat, commands, cancellation | Fake HTTP integration tests | Live llama-server request |
| Metrics | Usage/timings plus Prometheus per-request deltas | Parser and SSE integration tests | Live throughput/MTP report |
| Benchmark | Warm-up plus three runs for n-max 4 and 6, medians, stability, and saved runtime identity | Source/contract checks | Live deterministic benchmark |
| Settings/reset | Strict validation, backups, atomic save, key rotation | Unit tests | Manager menu exercise |
| LAN security | Explicit consent, restricted firewall, key required, CORS allowlist | Settings and source checks | Elevated firewall test |
| Uninstall | Owned-path validation and separate typed model deletion | Script/contract checks | Installed machine removal test |
