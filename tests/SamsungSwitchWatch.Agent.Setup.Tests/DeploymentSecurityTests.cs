using System.Security.AccessControl;
using System.Security.Principal;
using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Agent.Setup.Infrastructure;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class DeploymentSecurityTests
{
    [Fact]
    public void WriteAllTextAtomic_CreatesAndReplacesUtf8WithoutLeavingTemporaryFiles()
    {
        using var folder = new TemporaryFolder();
        var fileSystem = new PhysicalSetupFileSystem();
        var path = Path.Combine(folder.Path, "journal.json");

        fileSystem.WriteAllTextAtomic(path, "첫 번째");
        fileSystem.WriteAllTextAtomic(path, "두 번째");

        Assert.Equal("두 번째", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(folder.Path, ".journal.json.*.tmp*"));
    }

    [Fact]
    public void EnsureDirectoryWritable_LeavesNoProbeOrNewEmptyDirectory()
    {
        using var folder = new TemporaryFolder();
        var fileSystem = new PhysicalSetupFileSystem();
        var missingParent = folder.Combine("new-parent");
        var missing = Path.Combine(missingParent, "product", "agent");

        fileSystem.EnsureDirectoryWritable(missing);

        Assert.False(Directory.Exists(missing));
        Assert.False(Directory.Exists(missingParent));
        Assert.Empty(Directory.GetFiles(folder.Path, ".samsung-switch-watch-write-*.tmp"));

        fileSystem.EnsureDirectoryWritable(folder.Path);

        Assert.Empty(Directory.GetFiles(folder.Path, ".samsung-switch-watch-write-*.tmp"));
    }

    [Fact]
    public void IsReparsePoint_DetectsJunctionOrSymbolicLinkAttribute()
    {
        Assert.True(PhysicalSetupFileSystem.IsReparsePoint(
            FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.False(PhysicalSetupFileSystem.IsReparsePoint(FileAttributes.Directory));
    }

    [Fact]
    public void FreshDataDirectoryAdoption_AcceptsOnlyExistingEmptyDirectory()
    {
        using var folder = new TemporaryFolder();

        Assert.True(PhysicalSetupFileSystem.IsEmptyNonReparseDirectory(folder.Path));

        var filePath = Path.Combine(folder.Path, "unexpected.txt");
        File.WriteAllText(filePath, "unexpected");
        Assert.False(PhysicalSetupFileSystem.IsEmptyNonReparseDirectory(folder.Path));

        File.Delete(filePath);
        Directory.CreateDirectory(Path.Combine(folder.Path, "unexpected-child"));
        Assert.False(PhysicalSetupFileSystem.IsEmptyNonReparseDirectory(folder.Path));
    }

    [Fact]
    public void FreshDataDirectoryAdoption_RejectsMissingDirectory()
    {
        using var folder = new TemporaryFolder();
        var missing = Path.Combine(folder.Path, "missing");

        Assert.False(PhysicalSetupFileSystem.IsEmptyNonReparseDirectory(missing));
    }

    [Fact]
    public void IsAllowedOwner_RequiresExplicitTrustedOwner()
    {
        var administrators =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var user = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        Assert.True(PhysicalSetupFileSystem.IsAllowedOwner(
            administrators,
            [administrators]));
        Assert.False(PhysicalSetupFileSystem.IsAllowedOwner(
            user,
            [administrators]));
    }

    [Fact]
    public void ServiceDaclAudit_RejectsStopGrantToOrdinaryUsers()
    {
        var administrators =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var dacl = new RawAcl(GenericAcl.AclRevision, 1);
        dacl.InsertAce(
            0,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                0x20,
                users,
                false,
                null));

        Assert.True(WindowsServiceManager.GrantsStopToUnexpectedPrincipal(
            dacl,
            administrators,
            system,
            service));
    }

    [Fact]
    public void ServiceDaclAudit_AcceptsStopGrantOnlyForAdministratorsAndSystem()
    {
        var administrators =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        var dacl = new RawAcl(GenericAcl.AclRevision, 2);
        dacl.InsertAce(
            0,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                0x20,
                administrators,
                false,
                null));
        dacl.InsertAce(
            1,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                0x20,
                system,
                false,
                null));

        Assert.False(WindowsServiceManager.GrantsStopToUnexpectedPrincipal(
            dacl,
            administrators,
            system,
            service));
    }

    [Fact]
    public void HasBroadWriteAccess_RejectsUsersModifyGrant()
    {
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify,
            AccessControlType.Allow));

        Assert.True(PhysicalSetupFileSystem.HasUntrustedWriteAccess(
            security,
            [new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]));
    }

    [Fact]
    public void HasBroadWriteAccess_AllowsUsersReadExecuteOnly()
    {
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));

        Assert.False(PhysicalSetupFileSystem.HasUntrustedWriteAccess(
            security,
            [new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]));
    }

    [Fact]
    public void HasUntrustedWriteAccess_RejectsDirectStandardUserWriteGrant()
    {
        var security = new FileSecurity();
        var standardUser = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        security.AddAccessRule(new FileSystemAccessRule(
            standardUser,
            FileSystemRights.WriteData,
            AccessControlType.Allow));

        Assert.True(PhysicalSetupFileSystem.HasUntrustedWriteAccess(
            security,
            [new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]));
    }

    [Fact]
    public void LegacyLocalServiceOwnership_IsAllowedOnlyWhenServiceIsStopped()
    {
        Assert.True(ServiceAccountContract.AllowsLegacyLocalServiceDataOwner(
            Service(@"NT AUTHORITY\LocalService", running: false)));
        Assert.False(ServiceAccountContract.AllowsLegacyLocalServiceDataOwner(
            Service(@"NT AUTHORITY\LocalService", running: true)));
        Assert.False(ServiceAccountContract.AllowsLegacyLocalServiceDataOwner(
            Service(@"NT SERVICE\SamsungSwitchWatchAgent", running: false)));
    }

    [Fact]
    public void ReceiptFile_RemainsAdministratorsOnlyDuringDataAclMigration()
    {
        const string root = @"C:\ProgramData\SamsungSwitchWatch";

        Assert.False(PhysicalSetupFileSystem.ShouldGrantServiceAccess(
            root,
            Path.Combine(root, "install-receipt.json"),
            DirectoryAccessKind.AgentDataModify));
        Assert.True(PhysicalSetupFileSystem.ShouldGrantServiceAccess(
            root,
            Path.Combine(root, "agent-identity.json"),
            DirectoryAccessKind.AgentDataModify));
    }

    [Fact]
    public void AccessNormalization_AcceptsCanonicalProgramFileAcl()
    {
        const string root = @"C:\Program Files\SamsungSwitchWatch\Agent";
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = PhysicalSetupFileSystem.CreateServiceSid(
            SetupConstants.ServiceName);
        var security = FileSecurityWithRules(
            administrators,
            (administrators, FileSystemRights.FullControl),
            (system, FileSystemRights.FullControl),
            (service, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize));

        Assert.False(PhysicalSetupFileSystem.NeedsAccessNormalization(
            root,
            Path.Combine(root, SetupConstants.AgentExecutableName),
            DirectoryAccessKind.ProgramReadExecute,
            security,
            administrators,
            system,
            service));
    }

    [Fact]
    public void AccessNormalization_RequiresDirectoryRulesToPropagate()
    {
        const string root = @"C:\Program Files\SamsungSwitchWatch\Agent";
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = PhysicalSetupFileSystem.CreateServiceSid(
            SetupConstants.ServiceName);
        var canonical = DirectorySecurityWithRules(
            administrators,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            (administrators, FileSystemRights.FullControl),
            (system, FileSystemRights.FullControl),
            (service, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize));
        var nonPropagating = DirectorySecurityWithRules(
            administrators,
            InheritanceFlags.None,
            (administrators, FileSystemRights.FullControl),
            (system, FileSystemRights.FullControl),
            (service, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize));
        var child = Path.Combine(root, "runtime");

        Assert.False(PhysicalSetupFileSystem.NeedsAccessNormalization(
            root,
            child,
            DirectoryAccessKind.ProgramReadExecute,
            canonical,
            administrators,
            system,
            service));
        Assert.True(PhysicalSetupFileSystem.NeedsAccessNormalization(
            root,
            child,
            DirectoryAccessKind.ProgramReadExecute,
            nonPropagating,
            administrators,
            system,
            service));
    }

    [Fact]
    public void AccessNormalization_RejectsUnexpectedReadOnlySidAndServiceOvergrant()
    {
        const string root = @"C:\Program Files\SamsungSwitchWatch\Agent";
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = PhysicalSetupFileSystem.CreateServiceSid(
            SetupConstants.ServiceName);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var unexpectedSid = FileSecurityWithRules(
            administrators,
            (administrators, FileSystemRights.FullControl),
            (system, FileSystemRights.FullControl),
            (service, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize),
            (users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize));
        var serviceOvergrant = FileSecurityWithRules(
            administrators,
            (administrators, FileSystemRights.FullControl),
            (system, FileSystemRights.FullControl),
            (service, FileSystemRights.FullControl));

        Assert.True(PhysicalSetupFileSystem.NeedsAccessNormalization(
            root,
            Path.Combine(root, SetupConstants.AgentExecutableName),
            DirectoryAccessKind.ProgramReadExecute,
            unexpectedSid,
            administrators,
            system,
            service));
        Assert.True(PhysicalSetupFileSystem.NeedsAccessNormalization(
            root,
            Path.Combine(root, SetupConstants.AgentExecutableName),
            DirectoryAccessKind.ProgramReadExecute,
            serviceOvergrant,
            administrators,
            system,
            service));
    }

    [Fact]
    public void AccessNormalization_RejectsTrustedButNonCanonicalOwner()
    {
        const string root = @"C:\Program Files\SamsungSwitchWatch\Agent";
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = PhysicalSetupFileSystem.CreateServiceSid(
            SetupConstants.ServiceName);
        var security = FileSecurityWithRules(
            system,
            (administrators, FileSystemRights.FullControl),
            (system, FileSystemRights.FullControl),
            (service, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize));

        Assert.True(PhysicalSetupFileSystem.NeedsAccessNormalization(
            root,
            Path.Combine(root, SetupConstants.AgentExecutableName),
            DirectoryAccessKind.ProgramReadExecute,
            security,
            administrators,
            system,
            service));
    }

    [Fact]
    public void AccessNormalization_RemovesServiceAceFromReceipt()
    {
        const string root = @"C:\ProgramData\SamsungSwitchWatch";
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = PhysicalSetupFileSystem.CreateServiceSid(
            SetupConstants.ServiceName);
        var security = FileSecurityWithRules(
            administrators,
            (administrators, FileSystemRights.FullControl),
            (system, FileSystemRights.FullControl),
            (service, FileSystemRights.Modify | FileSystemRights.Synchronize));

        Assert.True(PhysicalSetupFileSystem.NeedsAccessNormalization(
            root,
            Path.Combine(root, "install-receipt.json"),
            DirectoryAccessKind.AgentDataModify,
            security,
            administrators,
            system,
            service));
    }

    [Fact]
    public void EnsureDirectoryAccess_EnforcesNativeNtfsContractWithoutChangingContents()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator))
        {
            return;
        }

        using var folder = new TemporaryFolder();
        var fileSystem = new PhysicalSetupFileSystem();
        var programRoot = folder.Combine("program");
        var runtimeDirectory = Path.Combine(programRoot, "runtime");
        var programFile = Path.Combine(runtimeDirectory, "agent.keep");
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(programFile, "program-preserved");

        fileSystem.EnsureDirectoryAccess(
            programRoot,
            DirectoryAccessKind.ProgramReadExecute);

        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = PhysicalSetupFileSystem.CreateServiceSid(
            SetupConstants.ServiceName);
        Assert.False(PhysicalSetupFileSystem.NeedsAccessNormalization(
            programRoot,
            programRoot,
            DirectoryAccessKind.ProgramReadExecute,
            new DirectoryInfo(programRoot).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access),
            administrators,
            system,
            service));
        Assert.False(PhysicalSetupFileSystem.NeedsAccessNormalization(
            programRoot,
            runtimeDirectory,
            DirectoryAccessKind.ProgramReadExecute,
            new DirectoryInfo(runtimeDirectory).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access),
            administrators,
            system,
            service));
        Assert.False(PhysicalSetupFileSystem.NeedsAccessNormalization(
            programRoot,
            programFile,
            DirectoryAccessKind.ProgramReadExecute,
            new FileInfo(programFile).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access),
            administrators,
            system,
            service));

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var tamperedFileSecurity = new FileInfo(programFile).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        tamperedFileSecurity.AddAccessRule(new FileSystemAccessRule(
            users,
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        new FileInfo(programFile).SetAccessControl(tamperedFileSecurity);
        Assert.True(PhysicalSetupFileSystem.NeedsAccessNormalization(
            programRoot,
            programFile,
            DirectoryAccessKind.ProgramReadExecute,
            new FileInfo(programFile).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access),
            administrators,
            system,
            service));

        fileSystem.EnsureDirectoryAccess(
            programRoot,
            DirectoryAccessKind.ProgramReadExecute);

        Assert.False(PhysicalSetupFileSystem.NeedsAccessNormalization(
            programRoot,
            programFile,
            DirectoryAccessKind.ProgramReadExecute,
            new FileInfo(programFile).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access),
            administrators,
            system,
            service));
        Assert.Equal("program-preserved", File.ReadAllText(programFile));

        var dataRoot = folder.Combine("data");
        Directory.CreateDirectory(dataRoot);
        var identityFile = Path.Combine(dataRoot, "agent-identity.json");
        var receiptFile = Path.Combine(dataRoot, "install-receipt.json");
        File.WriteAllText(identityFile, "identity-preserved");
        File.WriteAllText(receiptFile, "receipt-preserved");

        fileSystem.EnsureDirectoryAccess(
            dataRoot,
            DirectoryAccessKind.AgentDataModify);

        Assert.False(PhysicalSetupFileSystem.NeedsAccessNormalization(
            dataRoot,
            identityFile,
            DirectoryAccessKind.AgentDataModify,
            new FileInfo(identityFile).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access),
            administrators,
            system,
            service));
        Assert.False(PhysicalSetupFileSystem.NeedsAccessNormalization(
            dataRoot,
            receiptFile,
            DirectoryAccessKind.AgentDataModify,
            new FileInfo(receiptFile).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access),
            administrators,
            system,
            service));
        Assert.Equal("identity-preserved", File.ReadAllText(identityFile));
        Assert.Equal("receipt-preserved", File.ReadAllText(receiptFile));

        fileSystem.EnsureDirectoryAccess(
            programRoot,
            DirectoryAccessKind.AdministratorOnly);

        Assert.False(PhysicalSetupFileSystem.NeedsAccessNormalization(
            programRoot,
            programFile,
            DirectoryAccessKind.AdministratorOnly,
            new FileInfo(programFile).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access),
            administrators,
            system,
            service));
        Assert.Equal("program-preserved", File.ReadAllText(programFile));
    }

    [Fact]
    public void CreateServiceSid_UsesWindowsServiceSidDerivation()
    {
        Assert.Equal(
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
            PhysicalSetupFileSystem.CreateServiceSid("TrustedInstaller").Value);
    }

    [Fact]
    public void VanishedEntryPolicy_IgnoresOnlyMissingNonRootChildren()
    {
        using var folder = new TemporaryFolder();
        var root = folder.Path;
        var child = Path.Combine(root, "gone.tmp");

        Assert.True(PhysicalSetupFileSystem.IsVanishedNonRootEntry(
            root,
            child,
            new FileNotFoundException()));
        Assert.False(PhysicalSetupFileSystem.IsVanishedNonRootEntry(
            root,
            root,
            new DirectoryNotFoundException()));
        Assert.False(PhysicalSetupFileSystem.IsVanishedNonRootEntry(
            root,
            child,
            new IOException()));
    }

    [Fact]
    public void TransientIoRetry_RetriesOnceAndReturnsSecondResult()
    {
        var attempts = 0;

        var result = PhysicalSetupFileSystem.RetryTransientIoOnce(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new IOException("transient sharing violation", 32);
            }

            return "ready";
        });

        Assert.Equal("ready", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void TransientIoRetry_PersistentIoStopsAfterTwoAttempts()
    {
        var attempts = 0;

        Assert.Throws<IOException>(() =>
            PhysicalSetupFileSystem.RetryTransientIoOnce<int>(() =>
            {
                attempts++;
                throw new IOException("persistent sharing violation", 32);
            }));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void TransientIoRetry_DoesNotRetryUnclassifiedIo()
    {
        var attempts = 0;

        Assert.Throws<IOException>(() =>
            PhysicalSetupFileSystem.RetryTransientIoOnce<int>(() =>
            {
                attempts++;
                throw new IOException("non-transient io", 5);
            }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void TransientIoRetry_DoesNotRetryAccessDenied()
    {
        var attempts = 0;

        Assert.Throws<UnauthorizedAccessException>(() =>
            PhysicalSetupFileSystem.RetryTransientIoOnce<int>(() =>
            {
                attempts++;
                throw new UnauthorizedAccessException("denied");
            }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void TransientIoRetry_DoesNotRetrySecurityException()
    {
        var attempts = 0;

        Assert.Throws<System.Security.SecurityException>(() =>
            PhysicalSetupFileSystem.RetryTransientIoOnce<int>(() =>
            {
                attempts++;
                throw new System.Security.SecurityException("denied");
            }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void InspectionRootCheck_RejectsMissingOrNonDirectoryRoot()
    {
        using var folder = new TemporaryFolder();
        var missing = Path.Combine(folder.Path, "missing");
        var file = Path.Combine(folder.Path, "not-a-directory.txt");
        File.WriteAllText(file, "fixture");

        var missingFailure = Assert.Throws<SetupException>(() =>
            PhysicalSetupFileSystem.EnsureInspectionRootAvailable(missing));
        var fileFailure = Assert.Throws<SetupException>(() =>
            PhysicalSetupFileSystem.EnsureInspectionRootAvailable(file));

        Assert.Equal(SetupErrorCodes.PathNotWritable, missingFailure.Code);
        Assert.Equal(SetupErrorCodes.PathNotWritable, fileFailure.Code);
    }

    private static ServiceSnapshot Service(string accountName, bool running) =>
        new(
            true,
            running,
            "\"agent.exe\" --service",
            2,
            accountName,
            "Agent",
            "Agent",
            1,
            ServiceRecoverySnapshot.Empty,
            [],
            running ? 1234 : 0);

    private static FileSecurity FileSecurityWithRules(
        SecurityIdentifier owner,
        params (SecurityIdentifier Identity, FileSystemRights Rights)[] rules)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        foreach (var rule in rules)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                rule.Identity,
                rule.Rights,
                AccessControlType.Allow));
        }

        return security;
    }

    private static DirectorySecurity DirectorySecurityWithRules(
        SecurityIdentifier owner,
        InheritanceFlags inheritanceFlags,
        params (SecurityIdentifier Identity, FileSystemRights Rights)[] rules)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        foreach (var rule in rules)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                rule.Identity,
                rule.Rights,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }
}
