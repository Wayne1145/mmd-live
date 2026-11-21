#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ReleaseDirectory,
    [Parameter(Mandatory = $true)][string] $ResultsDirectory,
    [string] $FixtureDirectory,
    [string] $ModelPath,
    [ValidateSet('Any', 'AMD', 'NVIDIA')][string] $Vendor = 'Any',
    [ValidateRange(-1, 128)][int] $AdapterIndex = -1
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).ProviderPath
$exe = Join-Path $release 'MmdLive.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing MmdLive.exe in $release" }
if (!$ModelPath) { $ModelPath = Join-Path $release 'studio/StudioPilot.pmx' }
$model = (Resolve-Path -LiteralPath $ModelPath).ProviderPath
$parent = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultsDirectory)
$run = Join-Path $parent ('capture-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($run) | Out-Null

function Get-FixtureFile([string] $Name, [string] $Sha256) {
    if ($FixtureDirectory) {
        $file = (Resolve-Path -LiteralPath (Join-Path $FixtureDirectory $Name)).ProviderPath
    } else {
        $file = Join-Path $run $Name
        Invoke-WebRequest -UseBasicParsing -Uri "https://storage.googleapis.com/mediapipe-assets/$Name" -OutFile $file
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $Sha256) {
        throw "The reference file checksum does not match: $Name"
    }
    return $file
}

function Invoke-PhotoProbe([string] $Photo, [string] $Name, [int] $Repeat) {
    $output = Join-Path $run $Name
    $arguments = @('--probe', '--size', '1080', '--repeat', [string]$Repeat,
        '--image', ('"' + $Photo + '"'), '--model', ('"' + $model + '"'), '--out', ('"' + $output + '"'))
    if ($AdapterIndex -ge 0) { $arguments += @('--adapter', [string]$AdapterIndex) }
    $process = Start-Process -FilePath $exe -WorkingDirectory $release -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "GPU probe failed (exit $($process.ExitCode)). See $output\error.txt" }
    return $output
}

function Measure-RenderChange([string] $Output) {
    $neutral = [Drawing.Bitmap]::FromFile((Join-Path $Output 'neutral.png'))
    try {
        $posed = [Drawing.Bitmap]::FromFile((Join-Path $Output 'avatar.png'))
        try {
            if ($posed.Width -ne 1920 -or $posed.Height -ne 1080) { throw 'Unexpected output resolution.' }
            $clear = 0; $visible = 0; $changed = 0
            for ($y = 0; $y -lt $posed.Height; $y += 8) {
                for ($x = 0; $x -lt $posed.Width; $x += 8) {
                    $pixel = $posed.GetPixel($x, $y)
                    if ($pixel.A -eq 0) { $clear++ } else { $visible++ }
                    if ($pixel.ToArgb() -ne $neutral.GetPixel($x, $y).ToArgb()) { $changed++ }
                }
            }
        } finally { $posed.Dispose() }
    } finally { $neutral.Dispose() }
    if ($clear -lt 32 -or $visible -lt 32 -or $changed -lt 16) {
        throw "Expected a moving avatar on a transparent background. Inspect $Output\avatar.png and neutral.png."
    }
    return [ordered]@{transparent = $clear; visible = $visible; changed = $changed}
}

function Measure-Hand([string] $Name, [string] $Photo, [string] $Reference, [bool] $Mirrored) {
    $baseline = Get-Content -Raw -LiteralPath $Reference
    $pattern = '(?s)landmark\s*\{\s*x:\s*([-+\d.eE]+)\s*y:\s*([-+\d.eE]+)\s*z:'
    # The first 21 rows are image-normalized positions. The next 21 are world coordinates.
    $points = [regex]::Matches($baseline, $pattern)
    if ($points.Count -lt 21 -or $baseline -notmatch 'label:\s*"Right"') { throw "Invalid hand reference: $Name" }
    if ($Mirrored) {
        $Name = "$Name-mirrored"
        $image = [Drawing.Bitmap]::FromFile($Photo)
        try {
            $Photo = Join-Path $run "$Name.png"
            $image.RotateFlip([Drawing.RotateFlipType]::RotateNoneFlipX)
            $image.Save($Photo, [Drawing.Imaging.ImageFormat]::Png)
        } finally { $image.Dispose() }
    }
    $output = Invoke-PhotoProbe $Photo $Name 1
    $motion = Get-Content -Raw -LiteralPath (Join-Path $output 'motion.json') | ConvertFrom-Json
    if ($motion.Hands.Count -ne 1 -or $motion.Hands[0].Points.Count -ne 21 -or $motion.Hands[0].Left -ne $Mirrored) {
        throw "Detected hand or anatomical side did not match: $Name"
    }
    $errors = for ($i = 0; $i -lt 21; $i++) {
        $expectedX = [double]::Parse($points[$i].Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
        if ($Mirrored) { $expectedX = 1 - $expectedX }
        $expectedY = [double]::Parse($points[$i].Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
        $actual = $motion.Hands[0].Points[$i]
        [Math]::Max([Math]::Abs($actual.X / $motion.Width - $expectedX), [Math]::Abs($actual.Y / $motion.Height - $expectedY))
    }
    $maxError = ($errors | Measure-Object -Maximum).Maximum
    $meanError = ($errors | Measure-Object -Average).Average
    if ($maxError -gt .05 -or $meanError -gt .025) { throw "Unexpected landmark locations in $Name (max $maxError, mean $meanError)." }
    return [ordered]@{sample = $Name; left = $motion.Hands[0].Left; count = 21; meanAxisError = $meanError; maxAxisError = $maxError}
}

$portraitHash = '70cbeb38e198c9862202e0979c21a99b40ca980d3e7b250176c85b1636a40f12'
$portrait = Get-FixtureFile 'woman_hands.jpg' $portraitHash
$output = Invoke-PhotoProbe $portrait 'portrait' 12
$probe = Get-Content -Raw -LiteralPath (Join-Path $output 'report.json') | ConvertFrom-Json
if ($probe.Backend -ne 'DirectML' -or $probe.CpuFallback -ne $false) { throw 'GPU inference provider check failed.' }
$adapter = Get-CimInstance -ClassName Win32_VideoController | Where-Object { $_.Name -eq $probe.Adapter } | Select-Object -First 1
if (!$adapter) { throw "Windows could not identify the selected DXGI adapter: $($probe.Adapter)" }
$pci = [regex]::Match($adapter.PNPDeviceID, 'VEN_([0-9A-F]{4})', 'IgnoreCase').Groups[1].Value
$hardware = switch ($pci) { '1002' { 'AMD' } '10DE' { 'NVIDIA' } default { $pci } }
if ($Vendor -ne 'Any' -and $hardware -ne $Vendor) { throw "Selected $hardware adapter, expected $Vendor. Check -AdapterIndex." }
$hands = @($probe.Hands)
if ($probe.Face -ne 478 -or $probe.Pose -ne 33 -or $hands.Count -ne 2 -or @($hands | Where-Object { $_.Landmarks -ne 21 }).Count) {
    throw "Unexpected landmark counts. See $output\report.json"
}
if (@($probe.timings).Count -ne 12) { throw 'Missing inference samples.' }
$render = Measure-RenderChange $output
$cases = @(
    @('thumb_up', '5d673c081ab13b8a1812269ff57047066f9c33c07db5f4178089e8cb3fdc0291', 'feddaa81e188b9bceae12a96766f71e8ff3b2b316b4a31d64054d8a329e6015e'),
    @('pointing_up', 'ecf8ca2611d08fa25948a4fc10710af9120e88243a54da6356bacea17ff3e36e', '6bfcd360c0caa82559396d387ac30e1d59efab3b3d96b5512f4f018d0abae7c4')
)
$handChecks = foreach ($case in $cases) {
    $photo = Get-FixtureFile ($case[0] + '.jpg') $case[1]
    $reference = Get-FixtureFile ($case[0] + '_landmarks.pbtxt') $case[2]
    foreach ($mirrored in @($false, $true)) { Measure-Hand $case[0] $photo $reference $mirrored }
}
$os = Get-CimInstance Win32_OperatingSystem
$warm = @($probe.timings | Select-Object -Skip 1 | Sort-Object { [double]$_ })
$result = [ordered]@{
    version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
    assemblySha256 = (Get-FileHash -LiteralPath (Join-Path $release 'MmdLive.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    os = $os.Caption; build = $os.BuildNumber; architecture = $os.OSArchitecture
    adapter = $probe.Adapter; vendor = $hardware; driver = $adapter.DriverVersion
    modelSha256 = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant()
    referenceSha256 = $portraitHash; width = 1920; height = 1080; render = $render
    face = $probe.Face; pose = $probe.Pose; hands = $hands.Count; handChecks = @($handChecks)
    medianWarmInferenceMs = [Math]::Round([double]$warm[[int](($warm.Count - 1) / 2)], 2)
}
$file = Join-Path $run 'station.json'
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $file -Encoding UTF8
Write-Output $file
