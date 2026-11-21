param([string]$Configuration='Release',[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'artifacts/publish/win-x64'}
& (Join-Path $PSScriptRoot 'fetch-models.ps1')
Push-Location -LiteralPath $root
try {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') restore src/MmdLive/MmdLive.csproj --locked-mode --runtime win-x64 '-p:SelfContained=true'
    if($LASTEXITCODE){throw 'NuGet restore failed'}
    & (Join-Path $PSScriptRoot 'dotnet.ps1') publish src/MmdLive/MmdLive.csproj --no-restore -c $Configuration --runtime win-x64 --self-contained true -o $OutputDirectory '-p:DebugSymbols=false' '-p:DebugType=None'
    if($LASTEXITCODE){throw 'Publish failed'}
    Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'THIRD-PARTY.md'),(Join-Path $root 'LICENSE') -Destination $OutputDirectory -Force
    $licenses=Join-Path $OutputDirectory 'licenses'
    [IO.Directory]::CreateDirectory($licenses)|Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root 'assets/licenses') -File | Copy-Item -Destination $licenses -Force
    Copy-Item -LiteralPath (Join-Path $root 'vendor/Coocoo3D/LICENSE') -Destination (Join-Path $licenses 'Coocoo3D.txt') -Force
    Copy-Item -LiteralPath (Join-Path $root 'assets/model-manifest.json') -Destination $OutputDirectory -Force
    $locator=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if(Test-Path -LiteralPath $locator){
        $vs=& $locator -latest -products '*' -property installationPath
        if($vs){
            $redist=Join-Path $vs 'VC/Redist/MSVC'
            if(Test-Path -LiteralPath $redist){
                $crt=Get-ChildItem -LiteralPath $redist -Directory | Where-Object {$_.Name -match '^\d+(\.\d+)+$'} | Sort-Object {[version]$_.Name} -Descending | ForEach-Object { Get-ChildItem -Path (Join-Path $_.FullName 'x64/Microsoft.VC*.CRT') -Directory -ErrorAction SilentlyContinue } | Select-Object -First 1
                if($crt){Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' -File | Copy-Item -Destination $OutputDirectory -Force}
            }
        }
    }
    Write-Output (Join-Path $OutputDirectory 'MmdLive.exe')
} finally {Pop-Location}
