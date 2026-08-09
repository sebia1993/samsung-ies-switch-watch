param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateRange(2, 10)]
    [int]$Iterations = 3,

    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testProject = Join-Path $repoRoot `
    'tests\SamsungSwitchWatch.Agent.Setup.Tests\SamsungSwitchWatch.Agent.Setup.Tests.csproj'
$dotnet = Get-SswDotNet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    throw "Agent Setup test project is missing: $testProject"
}

$retryFilter = @(
    'FullyQualifiedName~DeployAsync_TransientPackageCopySharingViolationRetries',
    'FullyQualifiedName~DeployAsync_PersistentPackageCopySharingViolationStopsAfterFiveAttempts',
    'FullyQualifiedName~DeployAsync_UnclassifiedPackageCopyIoDoesNotRetry',
    'FullyQualifiedName~DeployAsync_TransientPostCopyHashReadLockRetries',
    'FullyQualifiedName~DeployAsync_TransientStagedRuntimeHashReadLockRetries',
    'FullyQualifiedName~DeployAsync_UnclassifiedPostCopyHashIoDoesNotRetry',
    'FullyQualifiedName~DeployAsync_PostCopyHashMismatchFailsWithoutActivation',
    'FullyQualifiedName~DeployAsync_TransientActivationMoveSharingViolationRetries',
    'FullyQualifiedName~DeployAsync_PersistentActivationMoveSharingViolationStopsAfterFiveAttempts',
    'FullyQualifiedName~DeployAsync_UnclassifiedActivationMoveIoDoesNotRetry',
    'FullyQualifiedName~DeployAsync_TransientRollbackFileMoveFailureRestoresPreviousService',
    'FullyQualifiedName~DeployAsync_PersistentRollbackFileMoveFailureNeverRestartsPreviousService',
    'FullyQualifiedName~DeployAsync_FreshHealthFailureRetriesTransientProgramMoveLock',
    'FullyQualifiedName~TransientIoRetry_'
) -join '|'

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    Write-SswStep "Agent Setup retry stability iteration $iteration of $Iterations"
    $testArguments = @(
        'test',
        $testProject,
        '-c',
        $Configuration,
        '--no-restore',
        '--filter',
        $retryFilter,
        '--logger',
        'console;verbosity=minimal'
    )
    if ($NoBuild) {
        $testArguments += '--no-build'
    }

    & $dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Agent Setup retry stability iteration failed: $iteration"
    }
}

Write-SswStep 'Agent Setup retry stability tests passed'
