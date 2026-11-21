$root = Split-Path $PSScriptRoot
$portable = Join-Path $root '.tools/dotnet/dotnet.exe'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $root '.tools/nuget'
$command = if (Test-Path $portable) { $portable } else { 'dotnet' }
& $command @args
exit $LASTEXITCODE
