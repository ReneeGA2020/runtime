param(
    [Parameter(Mandatory)][string] $DotNet,
    [Parameter(Mandatory)][string] $OriginalRuntimeRoot,
    [Parameter(Mandatory)][string] $CandidateRuntimeRoot,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $RebuiltBaselineRuntimeRoot,
    [switch] $IncludeRequired
)

$ErrorActionPreference = 'Stop'
$probeRoot = $PSScriptRoot
$runner = Join-Path $probeRoot 'Runner/bin/Release/net12.0/Runner.dll'
$fixture = Join-Path $probeRoot 'Fixture/bin/Release/net12.0/Fixture.dll'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

& $DotNet build (Join-Path $probeRoot 'Runner/Runner.csproj') -c Release --nologo -v:q -p:NETCoreAppMaximumVersion=12.0
if ($LASTEXITCODE -ne 0) { throw "Probe build failed: $LASTEXITCODE" }

$runs = @(
    @{ Name = 'original-private'; Root = $OriginalRuntimeRoot; Expected = 'baseline'; Modes = @('no', 'session', 'held', 'default', 'equivalent', 'accessor') },
    @{ Name = 'candidate-private'; Root = $CandidateRuntimeRoot; Expected = 'patched'; Modes = @('no', 'session', 'held', 'default', 'equivalent', 'accessor') }
)
if ($RebuiltBaselineRuntimeRoot) {
    $runs += @{ Name = 'rebuilt-baseline-private'; Root = $RebuiltBaselineRuntimeRoot; Expected = 'baseline'; Modes = @('no', 'session', 'accessor') }
}
if ($IncludeRequired) {
    $runs += @{ Name = 'original-required'; Root = $OriginalRuntimeRoot; Expected = 'baseline'; Modes = @('no', 'session'); Required = $true }
    $runs += @{ Name = 'candidate-required'; Root = $CandidateRuntimeRoot; Expected = 'patched'; Modes = @('no', 'session', 'held', 'default', 'equivalent'); Required = $true }
}

$results = @()
foreach ($run in $runs) {
    $hostPath = Join-Path $run.Root 'corerun.exe'
    foreach ($mode in $run.Modes) {
        $logPath = Join-Path $OutputDirectory "$($run.Name)-$mode.log"
        $arguments = @($runner, $mode, $fixture, $run.Expected)
        if ($run.Required) { $arguments += 'required' }
        & $hostPath @arguments 2>&1 | Tee-Object -FilePath $logPath
        $code = $LASTEXITCODE
        $results += [ordered]@{
            Name = "$($run.Name)-$mode"
            Executable = $hostPath
            Arguments = $arguments
            ExitCode = $code
            Log = $logPath
            LogSha256 = (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'probe-runs.json') -Encoding utf8
        if ($code -ne 0) { throw "Probe failed: $($run.Name)/$mode exit=$code" }
    }
}
