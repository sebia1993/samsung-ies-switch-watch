Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

function Assert-AgentSetupFileSystemContract {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) { throw $Message }
}

function Assert-ContractContainsAll {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string[]]$Needles
    )

    foreach ($needle in $Needles) {
        Assert-AgentSetupFileSystemContract -Condition $Text.Contains($needle) `
            -Message "$Name contract is missing: $needle"
    }
}

function Assert-ContractContainsNone {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string[]]$Needles
    )

    foreach ($needle in $Needles) {
        Assert-AgentSetupFileSystemContract -Condition (-not $Text.Contains($needle)) `
            -Message "$Name contract contains forbidden retry behavior: $needle"
    }
}

function Get-ContractBlock {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$StartMarker,
        [Parameter(Mandatory = $true)][string]$EndMarker
    )

    $start = $Text.IndexOf($StartMarker)
    $end = if ($start -ge 0) {
        $Text.IndexOf($EndMarker, $start + $StartMarker.Length)
    }
    else {
        -1
    }
    Assert-AgentSetupFileSystemContract -Condition (
        $start -ge 0 -and $end -gt $start
    ) -Message "$Name source block was not found."
    return $Text.Substring($start, $end - $start)
}

function Assert-OccurrenceCountAtLeast {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][int]$Minimum
    )

    $count = [Text.RegularExpressions.Regex]::Matches(
        $Text,
        [Text.RegularExpressions.Regex]::Escape($Needle)).Count
    Assert-AgentSetupFileSystemContract -Condition ($count -ge $Minimum) `
        -Message "$Name contract expected at least $Minimum occurrences of: $Needle"
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$contractsPath = Join-Path $repoRoot `
    'src\SamsungSwitchWatch.Agent.Setup\Deployment\SetupContracts.cs'
$diagnosticsPath = Join-Path $repoRoot `
    'src\SamsungSwitchWatch.Agent.Setup\Deployment\SetupDiagnosticsService.cs'
$orchestratorPath = Join-Path $repoRoot `
    'src\SamsungSwitchWatch.Agent.Setup\Deployment\AgentDeploymentOrchestrator.cs'
$fileSystemPath = Join-Path $repoRoot `
    'src\SamsungSwitchWatch.Agent.Setup\Infrastructure\PhysicalSetupFileSystem.cs'
$orchestratorTestsPath = Join-Path $repoRoot `
    'tests\SamsungSwitchWatch.Agent.Setup.Tests\AgentDeploymentOrchestratorTests.cs'
$securityTestsPath = Join-Path $repoRoot `
    'tests\SamsungSwitchWatch.Agent.Setup.Tests\DeploymentSecurityTests.cs'
$configurationTestsPath = Join-Path $repoRoot `
    'tests\SamsungSwitchWatch.Agent.Setup.Tests\ConfigurationAndInputTests.cs'

foreach ($path in @(
    $contractsPath,
    $diagnosticsPath,
    $orchestratorPath,
    $fileSystemPath,
    $orchestratorTestsPath,
    $securityTestsPath,
    $configurationTestsPath)) {
    Assert-AgentSetupFileSystemContract -Condition (Test-Path -LiteralPath $path -PathType Leaf) `
        -Message "Required native Agent Setup source is missing: $path"
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw -Encoding UTF8
$diagnostics = Get-Content -LiteralPath $diagnosticsPath -Raw -Encoding UTF8
$orchestrator = Get-Content -LiteralPath $orchestratorPath -Raw -Encoding UTF8
$fileSystem = Get-Content -LiteralPath $fileSystemPath -Raw -Encoding UTF8
$orchestratorTests = Get-Content -LiteralPath $orchestratorTestsPath -Raw -Encoding UTF8
$securityTests = Get-Content -LiteralPath $securityTestsPath -Raw -Encoding UTF8
$configurationTests = Get-Content -LiteralPath $configurationTestsPath -Raw -Encoding UTF8

Write-SswStep 'Native Agent Setup write-probe contract'
Assert-ContractContainsAll -Name 'Setup file-system interface' -Text $contracts -Needles @(
    'void EnsureDirectoryWritable(string path);'
)
$diagnosticsBlock = Get-ContractBlock `
    -Name 'Deployment path diagnostics' `
    -Text $diagnostics `
    -StartMarker 'internal static void ValidateDeploymentPathsForInstall(' `
    -EndMarker 'internal static async Task<ServiceSnapshot> CaptureServiceSnapshotAsync('
Assert-ContractContainsAll -Name 'Deployment path diagnostics' `
    -Text $diagnosticsBlock -Needles @(
        'fileSystem.CanCreateUnder(paths.InstallDirectory)',
        'fileSystem.CanCreateUnder(paths.DataDirectory)',
        'fileSystem.CanCreateUnder(paths.OperationsDirectory)',
        'fileSystem.EnsureDirectoryWritable(paths.InstallDirectory);',
        'fileSystem.EnsureDirectoryWritable(paths.DataDirectory);',
        'fileSystem.EnsureDirectoryWritable(paths.OperationsDirectory);'
    )
$writeProbeBlock = Get-ContractBlock `
    -Name 'Physical write probe' `
    -Text $fileSystem `
    -StartMarker 'public void EnsureDirectoryWritable(string path)' `
    -EndMarker 'public bool CanCreateUnder(string path)'
Assert-ContractContainsAll -Name 'Physical write probe' -Text $writeProbeBlock -Needles @(
    'directoriesMissingBeforeProbe',
    'Directory.CreateDirectory(fullPath);',
    'FileMode.CreateNew',
    'FileAccess.Write',
    'FileShare.None',
    'FileOptions.WriteThrough',
    'stream.Flush(flushToDisk: true);',
    'File.Delete(probePath);',
    'Directory.EnumerateFileSystemEntries(createdDirectory).Any()',
    'Directory.Delete(createdDirectory);'
)

Write-SswStep 'Native Agent Setup bounded transient retry contract'
Assert-ContractContainsAll -Name 'Windows transient classifier' -Text $fileSystem -Needles @(
    'private const int WindowsSharingViolation = 32;',
    'private const int WindowsLockViolation = 33;',
    'exception is IOException &&',
    '(exception.HResult & 0xFFFF) is WindowsSharingViolation or WindowsLockViolation'
)
Assert-ContractContainsAll -Name 'File mutation retry settings' -Text $orchestrator -Needles @(
    'private const int FileMutationMaxAttempts = 5;',
    'TimeSpan.FromMilliseconds(250);'
)
$copyBlock = Get-ContractBlock `
    -Name 'Package copy retry' `
    -Text $orchestrator `
    -StartMarker 'private async Task CopyFileWithRetryAsync(' `
    -EndMarker 'private async Task MoveDirectoryWithRetryAsync('
Assert-ContractContainsAll -Name 'Package copy retry' -Text $copyBlock -Needles @(
    'attempt <= FileMutationMaxAttempts',
    'sourceHash ??= fileSystem.ComputeSha256(source);',
    'fileSystem.CopyFile(source, destination, overwrite);',
    'fileSystem.ComputeSha256(destination)',
    'PhysicalSetupFileSystem.IsTransientFileSystemException(exception)',
    'await Task.Delay(FileMutationRetryDelay, cancellationToken);',
    'SetupErrorCodes.PackageHashMismatch'
)
Assert-ContractContainsNone -Name 'Package copy retry' -Text $copyBlock -Needles @(
    'UnauthorizedAccessException',
    'SecurityException',
    'catch (IOException)'
)
$moveBlock = Get-ContractBlock `
    -Name 'Directory move retry' `
    -Text $orchestrator `
    -StartMarker 'private async Task MoveDirectoryWithRetryAsync(' `
    -EndMarker 'private async Task<string> ComputeSha256WithRetryAsync('
Assert-ContractContainsAll -Name 'Directory move retry' -Text $moveBlock -Needles @(
    'attempt <= FileMutationMaxAttempts',
    'var sourceExists = fileSystem.DirectoryExists(source);',
    'var destinationExists = fileSystem.DirectoryExists(destination);',
    'if (!sourceExists && destinationExists)',
    'if (!sourceExists || destinationExists)',
    'fileSystem.MoveDirectory(source, destination);',
    'PhysicalSetupFileSystem.IsTransientFileSystemException(exception)',
    'await Task.Delay(',
    'FileMutationRetryDelay'
)
Assert-ContractContainsNone -Name 'Directory move retry' -Text $moveBlock -Needles @(
    'UnauthorizedAccessException',
    'SecurityException',
    'exception is IOException or UnauthorizedAccessException'
)
$hashBlock = Get-ContractBlock `
    -Name 'Staged hash retry' `
    -Text $orchestrator `
    -StartMarker 'private async Task<string> ComputeSha256WithRetryAsync(' `
    -EndMarker 'private static void AddRollbackFailure('
Assert-ContractContainsAll -Name 'Staged hash retry' -Text $hashBlock -Needles @(
    'attempt <= FileMutationMaxAttempts',
    'return fileSystem.ComputeSha256(path);',
    'PhysicalSetupFileSystem.IsTransientFileSystemException(exception)',
    'await Task.Delay(FileMutationRetryDelay, cancellationToken);'
)
Assert-ContractContainsNone -Name 'Staged hash retry' -Text $hashBlock -Needles @(
    'UnauthorizedAccessException',
    'SecurityException',
    'catch (IOException)'
)
Assert-OccurrenceCountAtLeast -Name 'Package staging copy' -Text $orchestrator `
    -Needle 'CopyFileWithRetryAsync(' -Minimum 3
Assert-OccurrenceCountAtLeast -Name 'Normal and rollback directory moves' -Text $orchestrator `
    -Needle 'MoveDirectoryWithRetryAsync(' -Minimum 5
Assert-AgentSetupFileSystemContract -Condition (
    -not $orchestrator.Contains('MoveDirectoryForRollbackAsync(')
) -Message 'Rollback must use the shared directory move retry policy.'

$cleanupBlock = Get-ContractBlock `
    -Name 'Evidence cleanup retry' `
    -Text $orchestrator `
    -StartMarker 'private static async Task<bool> TryDeleteEvidenceAsync(' `
    -EndMarker 'private static bool IsRollbackStageFailure('
Assert-ContractContainsAll -Name 'Evidence cleanup retry' -Text $cleanupBlock -Needles @(
    'PhysicalSetupFileSystem.IsTransientFileSystemException(exception)',
    'catch (UnauthorizedAccessException)',
    'catch (IOException)',
    'return false;'
)

Write-SswStep 'Native Agent Setup inherited ACL contract'
$accessBlock = Get-ContractBlock `
    -Name 'Directory ACL application' `
    -Text $fileSystem `
    -StartMarker 'public void EnsureDirectoryAccess(string path, DirectoryAccessKind accessKind)' `
    -EndMarker 'private static void ApplyDirectoryAccess('
Assert-ContractContainsAll -Name 'Directory ACL application' -Text $accessBlock -Needles @(
    'IsReparsePoint(File.GetAttributes(directory.FullName))',
    'ApplyDirectoryAccess(',
    'Directory.EnumerateFileSystemEntries(rootDirectory)',
    'NeedsAccessNormalization(',
    'ApplyEntryAccess('
)
$rootAclIndex = $accessBlock.IndexOf('ApplyDirectoryAccess(')
$childEnumerationIndex = $accessBlock.IndexOf(
    'Directory.EnumerateFileSystemEntries(rootDirectory)')
$normalizationIndex = $accessBlock.IndexOf('NeedsAccessNormalization(')
$entryWriteIndex = $accessBlock.IndexOf('ApplyEntryAccess(')
Assert-AgentSetupFileSystemContract -Condition (
    $rootAclIndex -ge 0 -and
    $childEnumerationIndex -gt $rootAclIndex -and
    $normalizationIndex -gt $childEnumerationIndex -and
    $entryWriteIndex -gt $normalizationIndex
) -Message 'Root ACL must be applied before descendants are inspected and rewritten conditionally.'
$normalizationBlock = Get-ContractBlock `
    -Name 'ACL normalization decision' `
    -Text $fileSystem `
    -StartMarker 'internal static bool NeedsAccessNormalization(' `
    -EndMarker 'internal static bool ShouldGrantServiceAccess('
Assert-ContractContainsAll -Name 'ACL normalization decision' `
    -Text $normalizationBlock -Needles @(
        '!owner.Equals(administrators)',
        'security is DirectorySecurity',
        'InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit',
        'allowedRuleIdentities',
        'HasCanonicalAllowRule(',
        'serviceRights != expectedServiceRights'
    )

Write-SswStep 'Native Agent Setup retry and ACL test coverage contract'
Assert-ContractContainsAll -Name 'Agent deployment retry tests' `
    -Text $orchestratorTests -Needles @(
        'DeployAsync_TransientPackageCopySharingViolationRetries',
        'DeployAsync_PersistentPackageCopySharingViolationStopsAfterFiveAttempts',
        'DeployAsync_UnclassifiedPackageCopyIoDoesNotRetry',
        'DeployAsync_TransientPostCopyHashReadLockRetries',
        'DeployAsync_UnclassifiedPostCopyHashIoDoesNotRetry',
        'DeployAsync_TransientActivationMoveSharingViolationRetries',
        'DeployAsync_PersistentActivationMoveSharingViolationStopsAfterFiveAttempts',
        'DeployAsync_PersistentRollbackFileMoveFailureNeverRestartsPreviousService'
    )
Assert-ContractContainsAll -Name 'Agent Setup ACL tests' -Text $securityTests -Needles @(
    'EnsureDirectoryWritable_LeavesNoProbeOrNewEmptyDirectory',
    'AccessNormalization_AcceptsCanonicalProgramFileAcl',
    'AccessNormalization_RequiresDirectoryRulesToPropagate',
    'AccessNormalization_RejectsUnexpectedReadOnlySidAndServiceOvergrant',
    'AccessNormalization_RejectsTrustedButNonCanonicalOwner',
    'AccessNormalization_RemovesServiceAceFromReceipt',
    'EnsureDirectoryAccess_EnforcesNativeNtfsContractWithoutChangingContents',
    'TransientIoRetry_DoesNotRetryAccessDenied',
    'TransientIoRetry_DoesNotRetrySecurityException'
)
Assert-ContractContainsAll -Name 'Agent Setup write-probe tests' `
    -Text $configurationTests -Needles @(
        'DeploymentWriteProbe_MapsAccessFailureToPathNotWritable',
        'DeploymentWriteProbe_ChecksEveryMutableProductRoot'
    )

Write-SswStep 'Native Agent Setup file-system contract tests passed'
