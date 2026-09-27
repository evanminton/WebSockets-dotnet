<#
  Builds WebSockets as a native C library (NativeAOT, no .NET runtime needed) and checks it from C:
    artifacts\native\WebSocketsNative-<version>-<rid>\
      include\websockets.h
      Release|Debug\shared   WebSocketsNative.dll + WebSocketsNative.lib (import library) + .pdb
      Release|Debug\static   WebSocketsNative.lib + runtime\ (NativeAOT runtime libraries + link.rsp to link with)
      demo\                  ws_demo.c; ws_demo.exe (with the DLL) and ws_demo_static.exe
    artifacts\native\WebSocketsNative-<version>-<rid>.zip
  Steps: managed tests, four NativeAOT publishes, export check against the header, then ws_demo.c compiled against
  the shared and the static library of both configurations and run against samples/EchoServer on loopback.
  Usage: .\build-native.ps1 [-Rid win-x64|win-arm64]   Log: artifacts\native\build-log.txt
  Needs the MSVC C++ build tools ("Desktop development with C++" in Visual Studio or Build Tools).
#>
param([string]$Rid = "win-x64", [int]$Port = 8765)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$proj = Join-Path $repo "src\WebSockets.Native\WebSockets.Native.csproj"
$out = Join-Path $repo "artifacts\native"
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out "build-log.txt"
"WebSockets native library build $(Get-Date -Format s) ($Rid)" | Set-Content $log

function Log([string]$text) { $text | Add-Content $log }
function Step([string]$text) { Write-Host $text; Log ""; Log "===== $text =====" }
function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments 2>&1 | ForEach-Object { "$_" } | Add-Content $log
    if ($LASTEXITCODE -ne 0) { throw "$exe exited with $LASTEXITCODE (see $log)" }
}

try {
    if ($Rid -notin "win-x64", "win-arm64") { throw "Unsupported RID ${Rid}: NativeAOT builds Linux/macOS libraries only on those OSes." }
    $target = if ($Rid -eq "win-arm64") { "arm64" } else { "x64" }
    $hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
    $vcArch = if ($hostArch -eq $target) { $hostArch } else { "${hostArch}_$target" }
    $runDemo = $hostArch -eq $target -or $hostArch -eq "arm64"

    # MSVC environment (NativeAOT links with link.exe; the demo compiles with cl.exe).
    $component = if ($target -eq "arm64") { "Microsoft.VisualStudio.Component.VC.Tools.ARM64" } else { "Microsoft.VisualStudio.Component.VC.Tools.x86.x64" }
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires $component -property installationPath } else { $null }
    if (-not $vs) { throw "MSVC build tools with $component not found. Install ""Desktop development with C++"" in Visual Studio or Build Tools." }
    # The ILCompiler's own toolchain lookup runs vswhere.exe from PATH inside a developer environment.
    $env:PATH = "$(Split-Path $vswhere);$env:PATH"
    cmd /c "`"$vs\VC\Auxiliary\Build\vcvarsall.bat`" $vcArch >nul && set" | ForEach-Object {
        if ($_ -match "^([^=]+)=(.*)$") { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2]) }
    }
    Log "vcvarsall $vcArch from $vs"
    git -C $repo log --oneline -1 2>&1 | Add-Content $log
    dotnet --version | Add-Content $log

    $version = (dotnet msbuild $proj -getProperty:Version -nologo).Trim()
    if (-not $version) { $version = "1.0.0" }
    $name = "WebSocketsNative-$version-$Rid"
    $stage = Join-Path $out $name
    $obj = Join-Path $out "obj\$Rid"
    $zip = Join-Path $out "$name.zip"
    Remove-Item -Recurse -Force $stage, $obj, $zip -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force "$stage\include", "$stage\demo", $obj | Out-Null
    Copy-Item "$repo\src\WebSockets.Native\include\websockets.h" "$stage\include\"

    Step "[1/5] Managed tests (Release)"
    Run dotnet @("test", "$repo\tests\WebSockets.Tests\WebSockets.Tests.csproj", "-c", "Release", "-nologo")

    Step "[2/5] NativeAOT publish: shared + static, Release + Debug ($Rid)"
    foreach ($config in "Release", "Debug") {
        foreach ($kind in "Shared", "Static") {
            $dir = "$stage\$config\$($kind.ToLower())"
            # Separate native intermediate/output folders so the shared and static links never reuse each other's files.
            Run dotnet @("publish", $proj, "-c", $config, "-r", $Rid, "-p:NativeLib=$kind",
                "-p:NativeIntermediateOutputPath=$obj\$config\$kind\", "-p:NativeOutputPath=$obj\$config\$kind\bin\",
                "-o", $dir, "-nologo", "-v", "minimal")
            # Publish leaves the shared library's import library behind in the native output folder.
            if ($kind -eq "Shared") { Copy-Item "$obj\$config\$kind\bin\WebSocketsNative.lib" $dir }
            # The managed library's .pdb/.xml are compiled into the native binary; they don't belong next to it.
            Remove-Item "$dir\WebSockets.pdb", "$dir\WebSockets.xml" -ErrorAction SilentlyContinue
            if (-not (Test-Path "$dir\WebSocketsNative.lib")) { throw "WebSocketsNative.lib missing from $dir" }
            if ($kind -eq "Shared" -and -not (Test-Path "$dir\WebSocketsNative.dll")) { throw "WebSocketsNative.dll missing from $dir" }
        }

        # What a static-library consumer links as well: the NativeAOT runtime libraries plus system libraries.
        $shared = "$stage\$config\shared"
        $runtime = "$stage\$config\static\runtime"
        New-Item -ItemType Directory -Force $runtime | Out-Null
        $rsp = [System.Collections.Generic.List[string]]::new()
        foreach ($lib in Get-Content "$shared\native-libs.txt") {
            Copy-Item $lib $runtime
            $rsp.Add((Split-Path $lib -Leaf))
        }
        if (Test-Path "$shared\system-libs.txt") {
            foreach ($lib in Get-Content "$shared\system-libs.txt") { $rsp.Add($(if ($lib -like "*.lib") { $lib } else { "$lib.lib" })) }
        }
        foreach ($lib in "advapi32", "bcrypt", "crypt32", "iphlpapi", "kernel32", "mswsock", "ncrypt", "normaliz", "ntdll", "ole32", "oleaut32", "secur32", "user32", "version", "ws2_32") {
            $rsp.Add("$lib.lib")
        }
        $rsp | Select-Object -Unique | Set-Content "$runtime\link.rsp"
        foreach ($f in "native-libs.txt", "system-libs.txt", "linker-args.txt") {
            if (Test-Path "$shared\$f") { Move-Item -Force "$shared\$f" $runtime }
        }
        Log "static runtime ($config):"; Get-Content "$runtime\link.rsp" | Add-Content $log
    }

    Step "[3/5] Exports of Release\shared\WebSocketsNative.dll against websockets.h"
    $exports = dumpbin /nologo /exports "$stage\Release\shared\WebSocketsNative.dll" |
        ForEach-Object { if ($_ -match "^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(ws_\w+)\b") { $Matches[1] } } | Sort-Object -Unique
    $declared = Select-String -Path "$stage\include\websockets.h" -Pattern "WS_CALL (ws_\w+)\(" -AllMatches |
        ForEach-Object { $_.Matches | ForEach-Object { $_.Groups[1].Value } } | Sort-Object -Unique
    $exports | Add-Content $log
    $missing = $declared | Where-Object { $_ -notin $exports }
    $extra = $exports | Where-Object { $_ -notin $declared }
    if ($missing) { throw "Declared in websockets.h but not exported: $($missing -join ', ')" }
    if ($extra) { throw "Exported but not declared in websockets.h: $($extra -join ', ')" }
    Log "$($exports.Count) exports, all declared in websockets.h"

    Step "[4/5] C demo against the shared and static libraries"
    Run dotnet @("build", "$repo\samples\EchoServer\EchoServer.csproj", "-c", "Release", "-nologo", "-v", "minimal", "-o", "$obj\echo")
    $server = if ($runDemo) { Start-Process dotnet -ArgumentList "`"$obj\echo\EchoServer.dll`"", $Port -PassThru -WindowStyle Hidden } else { $null }
    try {
        if ($server) {
            $deadline = (Get-Date).AddSeconds(30)
            while (-not (Test-NetConnection 127.0.0.1 -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue)) {
                if ((Get-Date) -gt $deadline) { throw "EchoServer did not start on port $Port" }
                Start-Sleep -Milliseconds 250
            }
        }
        $demo = "$repo\samples\native\ws_demo.c"
        foreach ($config in "Release", "Debug") {
            $t = "$obj\demo\$config"
            New-Item -ItemType Directory -Force "$t\shared", "$t\static" | Out-Null
            $sh = "$stage\$config\shared"
            $st = "$stage\$config\static"
            Log "--- $config shared: compile"
            Run cl @("/nologo", "/W4", "/WX", "/O2", "/MD", "/I", "$stage\include", $demo, "/Fo:$t\shared\ws_demo.obj", "/Fe:$t\shared\ws_demo.exe", "/link", "$sh\WebSocketsNative.lib")
            Copy-Item "$sh\WebSocketsNative.dll" "$t\shared\"
            Log "--- $config static: compile"
            # link.rsp's entries are passed one by one: PowerShell quotes an "@file" argument, and link.exe then won't expand it.
            $staticArgs = @("/nologo", "/W4", "/WX", "/O2", "/MT", "/DWS_STATIC", "/I", "$stage\include", $demo, "/Fo:$t\static\ws_demo.obj", "/Fe:$t\static\ws_demo.exe",
                "/link", "$st\WebSocketsNative.lib", "/LIBPATH:$st\runtime") + @(Get-Content "$st\runtime\link.rsp")
            Run cl $staticArgs
            if ($runDemo) {
                foreach ($kind in "shared", "static") {
                    Log "--- $config ${kind}: run"
                    Run "$t\$kind\ws_demo.exe" @("ws://127.0.0.1:$Port")
                    Write-Host "  $config $kind demo: $((Get-Content $log -Tail 1).Trim())"
                }
            }
            else { Log "Demo built for $target, not run on this $hostArch machine." }
            if ($config -eq "Release") {
                New-Item -ItemType Directory -Force "$stage\demo\shared" | Out-Null
                Copy-Item "$t\shared\ws_demo.exe", "$t\shared\WebSocketsNative.dll" "$stage\demo\shared\"
                Copy-Item "$t\static\ws_demo.exe" "$stage\demo\ws_demo_static.exe"
            }
        }
    }
    finally {
        if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    }
    Copy-Item $demo "$stage\demo\"

    Step "[5/5] Zip"
    Copy-Item "$repo\LICENSE" "$stage\"
    Copy-Item "$repo\src\WebSockets.Native\NATIVE.md" "$stage\README.md"
    $commit = git -C $repo rev-parse --short HEAD
    @(
        "WebSockets native library $version",
        "Commit: $commit",
        "Built: $(Get-Date -Format s)",
        ".NET SDK: $(dotnet --version)",
        "RID: $Rid (NativeAOT, no .NET runtime needed)",
        "Exports: $($exports.Count) ws_* functions"
    ) | Set-Content "$stage\BUILDINFO.txt"
    Compress-Archive -Path $stage -DestinationPath $zip
    Get-ChildItem -Recurse -File $stage | ForEach-Object { Log "$($_.Length)`t$($_.FullName)" }
    Log "$zip  $((Get-Item $zip).Length) bytes  SHA256 $((Get-FileHash -Algorithm SHA256 $zip).Hash)"
    Log "ALL OK"
    Write-Host "Done: $zip"
}
catch {
    Log ""; Log "FAILED: $_"
    Write-Host "FAILED: $_ (see $log)" -ForegroundColor Red
    exit 1
}
