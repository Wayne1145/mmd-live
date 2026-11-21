param([string]$Destination=(Join-Path (Split-Path $PSScriptRoot) 'assets/models'))
$ErrorActionPreference='Stop'
$manifest=Join-Path (Split-Path $PSScriptRoot) 'assets/model-manifest.json'
[IO.Directory]::CreateDirectory($Destination) | Out-Null
foreach($item in (Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json)) {
    $target=Join-Path $Destination $item.file
    if((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -eq $item.sha256) {continue}
    $download="$target.$([Guid]::NewGuid().ToString('N')).part"
    try {
        Invoke-WebRequest -Uri $item.url -OutFile $download
        if((Get-Item -LiteralPath $download).Length -ne $item.bytes -or (Get-FileHash -LiteralPath $download).Hash -ne $item.sha256) {throw "SHA-256 verification failed for $($item.file)"}
        Move-Item -LiteralPath $download -Destination $target -Force
        Write-Output "Verified $($item.file)"
    } finally {if(Test-Path -LiteralPath $download){Remove-Item -LiteralPath $download}}
}
