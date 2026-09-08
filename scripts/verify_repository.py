#!/usr/bin/env python3
from __future__ import annotations
import ast, hashlib, json, pathlib, re, sys, xml.etree.ElementTree as ET
from datetime import datetime, timezone

ROOT = pathlib.Path(__file__).resolve().parents[1]
REQUIRED = [
    'install.cmd','uninstall.cmd','run.cmd','diagnose-install.cmd','FlashNext.sln','README.md','INSTALL.md','LICENSE','THIRD_PARTY_NOTICES.md',
    'config/factory.settings.json','config/settings.schema.json','manifests/dependencies.lock.json','manifests/runtime.lock.json','manifests/model.lock.json','manifests/bootstrap.lock.json','patches/llama.cpp/0007-halo-compact-shared-mtp-sidecar.patch','patches/llama.cpp/0008-halo-mtp-request-state-lifecycle.patch','bootstrap/FlashNext.VulkanProbe.exe',
    'scripts/Common.ps1','scripts/Dependency-Probes.ps1','scripts/Test-BootstrapHardware.ps1','scripts/Install-FlashNext.ps1','scripts/Install-Dependencies.ps1','scripts/Build-FlashNext.ps1','scripts/Get-InstallDiagnostics.ps1','scripts/Install-MachineComponents.ps1','scripts/Build-Runtime.ps1','scripts/Resume-Install.ps1','scripts/Uninstall-FlashNext.ps1','scripts/Set-LanMode.ps1','scripts/model_download.py','scripts/Run-InferenceValidation.ps1','scripts/Run-CorrectnessValidation.ps1','scripts/inference_validation_client.py',
    'src/FlashNext.Core/FlashNext.Core.csproj','src/FlashNext.Infrastructure.Windows/FlashNext.Infrastructure.Windows.csproj','src/FlashNext.Manager/FlashNext.Manager.csproj','src/FlashNext.Dashboard/FlashNext.Dashboard.csproj','src/FlashNext.VulkanProbe/FlashNext.VulkanProbe.csproj',
    'tests/FlashNext.UnitTests/FlashNext.UnitTests.csproj','tests/FlashNext.IntegrationTests/FlashNext.IntegrationTests.csproj','tests/FlashNext.InstallationTests/FlashNext.InstallationTests.csproj',
    'tests/FlashNext.InstallationTests/VulkanSdkToolContractTests.cs','tests/FlashNext.InstallationTests/VulkanHardwareParsingTests.cs',
    'integrations/openai-compatible.md','integrations/csharp/Program.cs','integrations/csharp/Control.cs','integrations/python/client.py','integrations/python/control.py','integrations/node/client.mjs','integrations/opencode/opencode.jsonc',
]
TEXT_EXT = {'.cs','.ps1','.py','.cmd','.json','.md','.xml','.props','.csproj','.sln','.jsonc','.mjs'}

def load(relative: str):
    return json.loads((ROOT / relative).read_text(encoding='utf-8'))

def main() -> int:
    checks: list[dict[str, object]] = []
    failures: list[str] = []
    def record(name: str, ok: bool, detail: str) -> None:
        checks.append({'name':name,'success':ok,'detail':detail})
        if not ok: failures.append(f'{name}: {detail}')

    missing = [p for p in REQUIRED if not (ROOT / p).is_file()]
    record('required-files', not missing, 'all present' if not missing else ', '.join(missing))

    for path in ROOT.rglob('*.json'):
        try: json.loads(path.read_text(encoding='utf-8'))
        except Exception as exc: failures.append(f'json:{path.relative_to(ROOT)}: {exc}')
    record('json-syntax', not any(x.startswith('json:') for x in failures), 'JSON documents parsed')

    xml_errors=[]
    for pattern in ('*.csproj','*.props','*.xml'):
        for path in ROOT.rglob(pattern):
            try: ET.parse(path)
            except Exception as exc: xml_errors.append(f'{path.relative_to(ROOT)}: {exc}')
    record('xml-syntax', not xml_errors, 'XML documents parsed' if not xml_errors else '; '.join(xml_errors))

    python_errors=[]
    for path in ROOT.rglob('*.py'):
        try: ast.parse(path.read_text(encoding='utf-8'), filename=str(path))
        except Exception as exc: python_errors.append(f'{path.relative_to(ROOT)}: {exc}')
    record('python-syntax', not python_errors, 'Python sources compiled' if not python_errors else '; '.join(python_errors))

    markers=['TO'+'DO','FIX'+'ME','NotImplemented'+'Exception','place'+'holder','dum'+'my','omitted'+' method']
    specific='C:'+'\\Users\\'
    findings=[]
    ignored_parts = {'__pycache__', 'bin', 'obj', '.git', '.vs', 'artifacts', '.deployment', '.build-verify', '.test-temp'}
    for path in ROOT.rglob('*'):
        if not path.is_file() or path.suffix.lower() not in TEXT_EXT or any(part in ignored_parts for part in path.parts): continue
        text=path.read_text(encoding='utf-8',errors='replace')
        for marker in markers:
            if marker.lower() in text.lower(): findings.append(f'{path.relative_to(ROOT)} contains {marker}')
        if specific.lower() in text.lower(): findings.append(f'{path.relative_to(ROOT)} contains a user-specific Windows path')
        if '\x00' in text: findings.append(f'{path.relative_to(ROOT)} contains a null byte')
    record('source-completeness-markers', not findings, 'none found' if not findings else '; '.join(findings))

    runtime=load('manifests/runtime.lock.json')
    runtime_patches=runtime.get('patches',[])
    expected_runtime_patches={
      'patches/llama.cpp/0007-halo-compact-shared-mtp-sidecar.patch',
      'patches/llama.cpp/0008-halo-mtp-request-state-lifecycle.patch'}
    runtime_patch_ok=(len(runtime_patches)==len(expected_runtime_patches) and {x.get('path') for x in runtime_patches}==expected_runtime_patches and all(str(x.get('sha256','')).lower()==hashlib.sha256((ROOT/x['path']).read_bytes()).hexdigest() for x in runtime_patches))
    runtime_ok=(runtime.get('repository')=='https://github.com/halo-box/strix-llama.cpp' and runtime.get('commit')=='7449a0fe9710ab584c5f9a6d25e7a31eea2708b8' and len(runtime.get('commit',''))==40 and runtime_patch_ok and runtime.get('backend')=='Vulkan' and runtime.get('cmake',{}).get('supportedGenerators')==['Ninja with MSVC','Visual Studio 18 2026','Visual Studio 17 2022'] and 'matching' in runtime.get('cmake',{}).get('generatorPolicy',''))
    record('runtime-lock', runtime_ok, f"commit={runtime.get('commit')}")

    model=load('manifests/model.lock.json')
    expected_hashes={
      '4448186216b3af4cc558bbce2c3213f01608f8f8b2e5267a9767971dd3ec8082','3f342f1c1580473f1ee94ddd5b28206e8c07a70fa1a366f59d1d6c922919a6c9','56758f40269cad5cd9b0d3d6fbae0f40f6d5be6de49e4ab392dbe83157d9cbd3','753bda48b98ba4f1636134a90a967de1b2d3908a236c026e464777342e53510a','5ff54097406a905cf3a724c709124ceb0e3e10235ee862298969e91c96fa96e6','1f7b7f0b984cf065c604360c29c8098362ed61b290db0ff12c6f360bb1a8a980'}
    model_files=model.get('files',[])
    projector=next((x for x in model_files if x.get('kind')=='mmproj'),{})
    model_ok=(model.get('repository')=='unsloth/Qwen3.8-Flash-Next-GGUF' and model.get('revision')=='38bb39ee97821de2c9009abb7e93950eec396e66' and len(model_files)==6 and {x.get('sha256') for x in model_files}==expected_hashes and sum(x.get('kind')=='target' for x in model_files)==4 and sum(x.get('kind')=='mtp' for x in model_files)==1 and projector.get('path')=='mmproj-F16.gguf')
    record('model-lock', model_ok, f"revision={model.get('revision')}, files={len(model_files)}")

    deps=load('manifests/dependencies.lock.json')
    versions={x['id']:x['version'] for x in deps.get('packages',[])}
    expected_versions={'Git.Git':'2.55.0.3','Kitware.CMake':'4.4.2','Python.Python.3.13':'3.13.15','Microsoft.DotNet.SDK.10':'10.0.400','Microsoft.VisualStudio.2022.BuildTools':'17.14.38','KhronosGroup.VulkanSDK':'1.4.357.0'}
    python_versions={x['name']:x['version'] for x in deps.get('python',[])}
    expected_python_versions={'huggingface_hub':'1.28.0','hf_xet':'1.6.0'}
    dependency_ok=(versions==expected_versions and python_versions==expected_python_versions)
    record('dependency-lock', dependency_ok, json.dumps({'winget':versions,'python':python_versions},sort_keys=True))

    settings=load('config/factory.settings.json')
    profiles=settings.get('profiles',{})
    safe=(set(['coding-balanced','chat-fast','benchmark-deterministic','safe-fallback']).issubset(profiles) and settings.get('server',{}).get('host')=='127.0.0.1' and settings['server'].get('metrics') is True and settings['server'].get('jinja') is True and settings['server'].get('agent') is False and settings['server'].get('webUi') is False and settings['metrics'].get('includePromptOrResponse') is False)
    record('factory-safety', safe, 'profiles and loopback/auth-compatible server settings checked')

    common_script=(ROOT/'scripts/Common.ps1').read_text(encoding='utf-8')
    install_script=(ROOT/'scripts/Install-FlashNext.ps1').read_text(encoding='utf-8')
    hardware_probe=(ROOT/'src/FlashNext.Infrastructure.Windows/Hardware/WindowsHardwareProbe.cs').read_text(encoding='utf-8')
    hardware_identity=(ROOT/'src/FlashNext.Core/Services/HardwareIdentity.cs').read_text(encoding='utf-8')
    bootstrap_hardware_script=(ROOT/'scripts/Test-BootstrapHardware.ps1').read_text(encoding='utf-8')
    preflight_v2=(
        'GetPhysicallyInstalledSystemMemory' in common_script
        and 'Win32_PhysicalMemory' in common_script
        and 'Test-FlashNextGpuTarget' in install_script
        and "-notmatch 'Radeon 8060S'" not in install_script
        and 'HardwareIdentity.IsTargetGpu' in hardware_probe
        and 'OsVisibleMemoryBytes' in hardware_probe
        and '8060S' in hardware_identity
        and "projectRevision = '1.0.5'" in install_script
        and 'FlashNext.VulkanProbe.exe' in bootstrap_hardware_script
        and 'direct-vulkan-memory-probe' in bootstrap_hardware_script
        and 'VulkanProbeEvaluator' in hardware_probe
    )
    record('hardware-preflight-v2', preflight_v2, 'normalized GPU identity and installed-vs-OS-visible memory paths are wired')

    dependency_script=(ROOT/'scripts/Install-Dependencies.ps1').read_text(encoding='utf-8')
    dependency_probe_script=(ROOT/'scripts/Dependency-Probes.ps1').read_text(encoding='utf-8')
    build_script=(ROOT/'scripts/Build-FlashNext.ps1').read_text(encoding='utf-8')
    machine_script=(ROOT/'scripts/Install-MachineComponents.ps1').read_text(encoding='utf-8')
    diagnostics_script=(ROOT/'scripts/Get-InstallDiagnostics.ps1').read_text(encoding='utf-8')
    model_manager=(ROOT/'src/FlashNext.Infrastructure.Windows/Models/ModelManager.cs').read_text(encoding='utf-8')
    installer_v5=(
        '-EncodedCommand' in common_script
        and 'Write-FlashNextOperationResult' in common_script
        and "source','update','--name','winget" in common_script
        and "source','update','--name','winget','--disable-interactivity','--accept-source-agreements" not in common_script
        and "source','list','--disable-interactivity','--accept-source-agreements" not in common_script
        and 'Install-Dependencies.ps1' in install_script
        and 'Build-FlashNext.ps1' in install_script
        and 'build-install-result.json' in install_script
        and 'machine-install-result.json' in install_script
        and 'Initialize-FlashNextWinGetSource' in dependency_script
        and 'Get-FlashNextDependencyProbe' in dependency_script
        and 'Wait-FlashNextDependencyProbe' in dependency_script
        and 'Get-WinGetInstalledVersion' not in dependency_script
        and 'human-formatted winget list output is diagnostic only' in (ROOT/'manifests/dependencies.lock.json').read_text(encoding='utf-8').lower()
        and 'git version 2.55.0.windows.3' in dependency_probe_script
        and 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' in dependency_probe_script
        and 'Include\\vulkan\\vulkan.h' in dependency_probe_script
        and 'vulkaninfoSDK.exe' in dependency_probe_script
        and 'vulkaninfo.exe' in dependency_probe_script
        and 'Test-BootstrapHardware.ps1' in install_script
        and 'Visual Studio 18 2026' in (ROOT/'scripts/Build-Runtime.ps1').read_text(encoding='utf-8')
        and 'CMAKE_GENERATOR_INSTANCE' in (ROOT/'scripts/Build-Runtime.ps1').read_text(encoding='utf-8')
        and 'CMAKE_GENERATOR_INSTANCE:INTERNAL' in (ROOT/'scripts/Build-Runtime.ps1').read_text(encoding='utf-8')
        and 'FlashNext.VulkanProbe.exe' in bootstrap_hardware_script
        and 'direct-vulkan-memory-probe' in bootstrap_hardware_script
        and 'DEVICE_LOCAL' in bootstrap_hardware_script
        and '90GB' in bootstrap_hardware_script
        and 'TimeoutSeconds' in common_script
        and 'Stop-FlashNextProcessTree' in common_script
        and 'Convert-FlashNextInstallState' in common_script
        and 'dotnet-restore' in build_script
        and 'dotnet-tests' in build_script
        and 'manager-publish' in build_script
        and 'Build-Runtime.ps1' in build_script
        and 'inventories' in build_script
        and 'build-result-validation' in machine_script
        and 'staging-integrity-validation' in machine_script
        and 'atomic-activation' in machine_script
        and 'winget' not in machine_script.lower()
        and 'dotnet' not in machine_script.lower()
        and 'pip' not in machine_script.lower()
        and 'build-runtime.ps1' not in machine_script.lower()
        and 'cmake' not in machine_script.lower()
        and 'Structured dependency-stage result' in diagnostics_script
        and 'Structured build-stage result' in diagnostics_script
        and 'Structured machine-stage result' in diagnostics_script
        and 'Environment.SpecialFolder.LocalApplicationData' in model_manager
        and 'Environment.SpecialFolder.ProgramFiles), "FlashNextManager", "python-env"' not in model_manager
    )
    record('installer-architecture-v5', installer_v5, 'dependencies use direct capability probes; builds run as the user; elevation is limited to verified atomic deployment')

    scripts='\n'.join((ROOT/p).read_text(encoding='utf-8') for p in ['scripts/Install-FlashNext.ps1','scripts/Install-Dependencies.ps1','scripts/Dependency-Probes.ps1','scripts/Test-BootstrapHardware.ps1','scripts/Build-FlashNext.ps1','scripts/Install-MachineComponents.ps1','scripts/Build-Runtime.ps1','scripts/model_download.py'])
    server_arguments=(ROOT/'src/FlashNext.Core/Services/ServerArgumentBuilder.cs').read_text(encoding='utf-8')
    lock_refs=('runtime.lock.json' in scripts and 'model.lock.json' in scripts)
    features=(all(token in server_arguments for token in ['--spec-draft-n-max','--api-key-file','--metrics','--jinja','--no-agent','--no-ui']) and '--accept-license' in scripts)
    record('installer-feature-wiring', lock_refs and features, 'immutable pins and required runtime/model options are wired')

    license_errors=[]
    for rel in ['licenses/Qwen-Community-License-1.0.txt','licenses/llama.cpp-MIT.txt']:
        path=ROOT/rel
        if not path.is_file() or path.stat().st_size<200: license_errors.append(f'{rel} is missing or too small')
        elif '<html' in path.read_text(encoding='utf-8',errors='ignore').lower(): license_errors.append(f'{rel} is HTML rather than license text')
    record('third-party-license-texts', not license_errors, 'license texts present' if not license_errors else '; '.join(license_errors))

    bootstrap_lock_path=ROOT/'manifests'/'bootstrap.lock.json'
    bootstrap_exe=ROOT/'bootstrap'/'FlashNext.VulkanProbe.exe'
    bootstrap_ok=False
    bootstrap_detail='missing'
    if bootstrap_lock_path.is_file() and bootstrap_exe.is_file():
        try:
            bootstrap_lock=json.loads(bootstrap_lock_path.read_text(encoding='utf-8'))
            digest=hashlib.sha256(bootstrap_exe.read_bytes()).hexdigest()
            bootstrap_ok=(digest==str(bootstrap_lock.get('sha256','')).lower() and bootstrap_exe.stat().st_size==int(bootstrap_lock.get('bytes',-1)))
            bootstrap_detail=f"sha256={digest}, bytes={bootstrap_exe.stat().st_size}"
        except Exception as exc:
            bootstrap_detail=str(exc)
    record('bootstrap-vulkan-probe-lock', bootstrap_ok, bootstrap_detail)

    no_full_report=True
    for rel in ['scripts/Test-BootstrapHardware.ps1','src/FlashNext.Infrastructure.Windows/Hardware/WindowsHardwareProbe.cs']:
        text=(ROOT/rel).read_text(encoding='utf-8')
        if 'Arguments @()' in text and 'vulkaninfo' in text.lower():
            no_full_report=False
        if 'new ProcessSpec(vulkanInfo, []' in text:
            no_full_report=False
    record('no-full-vulkaninfo-report', no_full_report, 'installer and manager use the direct Vulkan memory probe')

    report={'generatedAtUtc':datetime.now(timezone.utc).isoformat(),'repository':ROOT.name,'success':not failures,'checks':checks,'limitations':['This platform-independent verifier does not compile .NET, parse PowerShell with the Windows engine, install dependencies, build Vulkan, download model weights, or exercise EVO-X2 hardware. Those checks run on Windows through Verify-Repository.ps1 and install/runtime smoke tests.']}
    output=ROOT/'artifacts'/'static-verification.json'
    output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    for check in checks: print(('PASS' if check['success'] else 'FAIL')+f"  {check['name']}: {check['detail']}")
    if failures:
        print('\nFailures:',file=sys.stderr)
        for failure in failures: print('- '+failure,file=sys.stderr)
    print(f'\nReport: {output}')
    return 0 if not failures else 1

if __name__=='__main__': raise SystemExit(main())
