# Strix Halo candidate patches

These patches are scoped to the separately pinned `halo-box/strix-llama.cpp`
candidate under `artifacts/halo-candidate/source`. They are not Laurent runtime
patches and must not be applied to `LaurentZuijdwijk/llama.cpp`.

`0001-official-shared-mtp-sidecar.patch` adds target-tensor borrowing and the
Qwen4Exp NextN hyperconnection mappings needed to load the official compact
shared-Q8 MTP sidecar. The build and measured binary remain identified by
`artifacts/halo-candidate/runtime.build.json`.
