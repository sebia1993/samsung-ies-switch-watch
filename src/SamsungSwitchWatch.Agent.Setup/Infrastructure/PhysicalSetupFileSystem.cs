using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Infrastructure;

public sealed class PhysicalSetupFileSystem : ISetupFileSystem
{
    private const int WindowsSharingViolation = 32;
    private const int WindowsLockViolation = 33;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) =>
        File.ReadAllText(Path.GetFullPath(path), Encoding.UTF8);

    public void WriteAllTextAtomic(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ??
                     throw new SetupException(
                         SetupErrorCodes.PathInvalid,
                         "파일 저장 경로가 올바르지 않습니다.");
        Directory.CreateDirectory(parent);

        var temporaryPath = Path.Combine(
            parent,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       Utf8WithoutBom,
                       bufferSize: 4096,
                       leaveOpen: true))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                var backupPath = $"{temporaryPath}.bak";
                File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true);
                File.Delete(backupPath);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            using var committed = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.WriteThrough);
            committed.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void CreateDirectory(string path) =>
        Directory.CreateDirectory(Path.GetFullPath(path));

    public void CopyFile(string source, string destination, bool overwrite) =>
        File.Copy(Path.GetFullPath(source), Path.GetFullPath(destination), overwrite);

    public void MoveDirectory(string source, string destination) =>
        Directory.Move(Path.GetFullPath(source), Path.GetFullPath(destination));

    public void DeleteDirectory(string path, bool recursive) =>
        Directory.Delete(Path.GetFullPath(path), recursive);

    public void DeleteFile(string path) => File.Delete(Path.GetFullPath(path));

    public void EnsureDirectoryWritable(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directoriesMissingBeforeProbe = new List<string>();
        var current = new DirectoryInfo(fullPath);
        while (!current.Exists && current.Parent is { } parent)
        {
            directoriesMissingBeforeProbe.Add(current.FullName);
            current = parent;
        }

        var probePath = Path.Combine(
            fullPath,
            $".samsung-switch-watch-write-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(fullPath);
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }

            // Directory.CreateDirectory can create several missing ancestors.
            // Remove only paths that were absent before the probe and remain
            // empty, deepest first. Stop as soon as another actor left data.
            foreach (var createdDirectory in directoriesMissingBeforeProbe)
            {
                if (!Directory.Exists(createdDirectory))
                {
                    continue;
                }
                if (Directory.EnumerateFileSystemEntries(createdDirectory).Any())
                {
                    break;
                }

                Directory.Delete(createdDirectory);
            }
        }
    }

    public bool CanCreateUnder(string path)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(path));
            while (!current.Exists && current.Parent is not null)
            {
                current = current.Parent;
            }

            return current.Exists;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException ||
            exception is IOException and not PathTooLongException)
        {
            return false;
        }
    }

    public void EnsureDirectoryAccess(string path, DirectoryAccessKind accessKind)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists)
        {
            directory.Create();
        }
        if (IsReparsePoint(File.GetAttributes(directory.FullName)))
        {
            throw new SetupException(
                SetupErrorCodes.PathUntrusted,
                "Agent 폴더 권한을 안전하게 적용할 수 없습니다.");
        }

        var administrators =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system =
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        // Keep the service SID available while auditing AdministratorOnly
        // trees so stale read/execute entries from the old install are also
        // removed from backup and failed evidence.
        var service = CreateServiceSid(SetupConstants.ServiceName);
        var serviceRights = accessKind == DirectoryAccessKind.AgentDataModify
            ? FileSystemRights.Modify
            : FileSystemRights.ReadAndExecute;
        var rootDirectory = directory.FullName;
        ApplyDirectoryAccess(
            rootDirectory,
            accessKind,
            administrators,
            system,
            service,
            serviceRights);

        // The protected root ACL is inherited by new children. Existing
        // children are inspected and rewritten only when their owner or ACL
        // violates the contract; clean files do not receive a needless
        // per-file SetAccessControl call on every update.
        var pending = new Queue<string>(
            Directory.EnumerateFileSystemEntries(rootDirectory));
        var checkedEntries = 0;
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (++checkedEntries > 4096 ||
                IsReparsePoint(File.GetAttributes(current)))
            {
                throw new SetupException(
                    SetupErrorCodes.PathUntrusted,
                    "Agent 폴더 권한을 안전하게 적용할 수 없습니다.");
            }

            var isDirectory = Directory.Exists(current);
            FileSystemSecurity security = isDirectory
                ? new DirectoryInfo(current).GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access)
                : new FileInfo(current).GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access);
            if (NeedsAccessNormalization(
                    rootDirectory,
                    current,
                    accessKind,
                    security,
                    administrators,
                    system,
                    service))
            {
                ApplyEntryAccess(
                    rootDirectory,
                    current,
                    isDirectory,
                    accessKind,
                    administrators,
                    system,
                    service,
                    serviceRights);
            }

            if (isDirectory)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(current))
                {
                    pending.Enqueue(child);
                }
            }
        }
    }

    private static void ApplyDirectoryAccess(
        string current,
        DirectoryAccessKind accessKind,
        SecurityIdentifier administrators,
        SecurityIdentifier system,
        SecurityIdentifier service,
        FileSystemRights serviceRights)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.SetOwner(administrators);
        AddDirectoryRule(security, system, FileSystemRights.FullControl);
        AddDirectoryRule(security, administrators, FileSystemRights.FullControl);
        if (accessKind != DirectoryAccessKind.AdministratorOnly)
        {
            AddDirectoryRule(security, service, serviceRights);
        }

        new DirectoryInfo(current).SetAccessControl(security);
    }

    private static void ApplyEntryAccess(
        string rootDirectory,
        string current,
        bool isDirectory,
        DirectoryAccessKind accessKind,
        SecurityIdentifier administrators,
        SecurityIdentifier system,
        SecurityIdentifier service,
        FileSystemRights serviceRights)
    {
        if (isDirectory)
        {
            ApplyDirectoryAccess(
                current,
                accessKind,
                administrators,
                system,
                service,
                serviceRights);
            return;
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.SetOwner(administrators);
        AddFileRule(security, system, FileSystemRights.FullControl);
        AddFileRule(security, administrators, FileSystemRights.FullControl);
        if (ShouldGrantServiceAccess(rootDirectory, current, accessKind))
        {
            AddFileRule(security, service, serviceRights);
        }

        new FileInfo(current).SetAccessControl(security);
    }

    internal static bool NeedsAccessNormalization(
        string rootDirectory,
        string entryPath,
        DirectoryAccessKind accessKind,
        FileSystemSecurity security,
        SecurityIdentifier administrators,
        SecurityIdentifier system,
        SecurityIdentifier? service)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        // Preflight may trust SYSTEM, the service SID or the current elevated
        // user long enough to adopt an existing tree. The normalized product
        // contract is stricter: every descendant is Administrators-owned.
        if (owner is null || !owner.Equals(administrators))
        {
            return true;
        }

        var accessRules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();
        var expectedInheritance = security is DirectorySecurity
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        if (!HasCanonicalAllowRule(
                accessRules,
                administrators,
                FileSystemRights.FullControl,
                expectedInheritance) ||
            !HasCanonicalAllowRule(
                accessRules,
                system,
                FileSystemRights.FullControl,
                expectedInheritance))
        {
            return true;
        }

        var serviceAccessExpected = service is not null &&
                                    ShouldGrantServiceAccess(
                                        rootDirectory,
                                        entryPath,
                                        accessKind);
        var allowedRuleIdentities = new HashSet<SecurityIdentifier>
        {
            administrators,
            system
        };
        if (serviceAccessExpected)
        {
            allowedRuleIdentities.Add(service!);
        }

        if (accessRules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                !allowedRuleIdentities.Contains(sid)))
        {
            return true;
        }

        FileSystemAccessRule[] serviceRules = service is null
            ? []
            : accessRules
                .Where(rule =>
                    rule.IdentityReference is SecurityIdentifier sid &&
                    sid.Equals(service))
                .ToArray();
        if (!serviceAccessExpected && serviceRules.Length > 0)
        {
            return true;
        }

        if (serviceAccessExpected)
        {
            var expectedServiceRights = accessKind == DirectoryAccessKind.AgentDataModify
                ? FileSystemRights.Modify
                : FileSystemRights.ReadAndExecute;
            // Windows persists Synchronize on ordinary allow ACEs even when
            // the requested FileSystemRights value omitted it.
            expectedServiceRights |= FileSystemRights.Synchronize;
            var serviceRights = serviceRules
                .Where(rule => rule.AccessControlType == AccessControlType.Allow)
                .Aggregate(
                    (FileSystemRights)0,
                    (rights, rule) => rights | rule.FileSystemRights);
            // Keep the service grant aligned with the directory contract. In
            // particular, a stale Modify/FullControl grant must not survive
            // on program files, and data files must not gain ACL ownership
            // rights merely because the service SID is trusted.
            if (serviceRights != expectedServiceRights ||
                !HasCanonicalAllowRule(
                    serviceRules,
                    service!,
                    expectedServiceRights,
                    expectedInheritance))
            {
                return true;
            }
        }

        return serviceAccessExpected && serviceRules.Length == 0;
    }

    internal static bool ShouldGrantServiceAccess(
        string rootDirectory,
        string entryPath,
        DirectoryAccessKind accessKind) =>
        accessKind != DirectoryAccessKind.AdministratorOnly &&
        !(accessKind == DirectoryAccessKind.AgentDataModify &&
          SamePath(
              entryPath,
              Path.Combine(rootDirectory, "install-receipt.json")));

    public void ValidateDeploymentPaths(
        DeploymentPaths paths,
        ServiceSnapshot service,
        IReadOnlyList<string> transactionPaths)
    {
        var expectedInstall = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "SamsungSwitchWatch",
            "Agent");
        var expectedData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SamsungSwitchWatch");
        var expectedOperations = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SamsungSwitchWatch-Operations");

        if (!SamePath(paths.InstallDirectory, expectedInstall) ||
            !SamePath(paths.DataDirectory, expectedData) ||
            !SamePath(paths.OperationsDirectory, expectedOperations))
        {
            throw new SetupException(
                SetupErrorCodes.PathUntrusted,
                "Agent 설치 또는 데이터 경로가 고정 제품 경로와 일치하지 않습니다.");
        }

        var installParent = Path.GetDirectoryName(Path.GetFullPath(paths.InstallDirectory))!;
        foreach (var transactionPath in transactionPaths)
        {
            var fullPath = Path.GetFullPath(transactionPath);
            var name = Path.GetFileName(fullPath);
            if (!SamePath(Path.GetDirectoryName(fullPath)!, installParent) ||
                !(name.StartsWith("Agent.__staging_", StringComparison.Ordinal) ||
                  name.StartsWith("Agent.__backup_", StringComparison.Ordinal) ||
                  name.StartsWith("Agent.__failed_", StringComparison.Ordinal)) ||
                Directory.Exists(fullPath) ||
                File.Exists(fullPath))
            {
                throw new SetupException(
                    SetupErrorCodes.PathUntrusted,
                    "설치 트랜잭션 경로를 안전하게 사용할 수 없습니다.");
            }
        }

        var existingPaths = new[]
        {
            installParent,
            paths.InstallDirectory,
            paths.DataDirectory,
            paths.OperationsDirectory
        }.Distinct(StringComparer.OrdinalIgnoreCase);
        var canAdoptFreshDataDirectory =
            !service.Exists &&
            IsEmptyNonReparseDirectory(paths.DataDirectory);
        foreach (var existingPath in existingPaths.Where(Directory.Exists))
        {
            var isInstall = SamePath(existingPath, paths.InstallDirectory);
            var isData = SamePath(existingPath, paths.DataDirectory);
            ValidateExistingDirectory(
                existingPath,
                allowServiceSid:
                    (service.Exists && (isInstall || isData)) ||
                    (isData && canAdoptFreshDataDirectory),
                allowLocalService: isData &&
                                   ServiceAccountContract
                                       .AllowsLegacyLocalServiceDataOwner(service),
                validateChildren: !SamePath(existingPath, installParent));
        }

        if (!service.Exists && Directory.Exists(paths.InstallDirectory))
        {
            throw new SetupException(
                SetupErrorCodes.PathUntrusted,
                "등록된 Agent 서비스 없이 기존 설치 폴더가 있어 자동으로 채택하지 않습니다.");
        }

        if (!service.Exists &&
            Directory.Exists(paths.DataDirectory) &&
            !HasOwnedAgentData(paths.DataDirectory) &&
            !canAdoptFreshDataDirectory)
        {
            throw new SetupException(
                SetupErrorCodes.PathUntrusted,
                "등록된 Agent 서비스 없이 신뢰할 수 있는 기존 데이터가 있어 자동으로 채택하지 않습니다.");
        }
    }

    public void ValidateRecoveryPaths(
        DeploymentPaths paths,
        ServiceSnapshot currentService,
        ServiceSnapshot previousService,
        bool allowFreshCreatedDataCleanup,
        IReadOnlyList<string> transactionPaths)
    {
        var expectedInstall = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "SamsungSwitchWatch",
            "Agent");
        var expectedData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SamsungSwitchWatch");
        var expectedOperations = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SamsungSwitchWatch-Operations");
        if (!SamePath(paths.InstallDirectory, expectedInstall) ||
            !SamePath(paths.DataDirectory, expectedData) ||
            !SamePath(paths.OperationsDirectory, expectedOperations))
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "보류 중인 설치의 제품 경로가 고정 경로와 일치하지 않습니다.");
        }

        var installParent = Path.GetDirectoryName(Path.GetFullPath(paths.InstallDirectory))!;
        foreach (var existingPath in new[]
                 {
                     installParent,
                     paths.InstallDirectory,
                     paths.DataDirectory,
                     paths.OperationsDirectory
                 }.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            var isInstall = SamePath(existingPath, paths.InstallDirectory);
            var isData = SamePath(existingPath, paths.DataDirectory);
            ValidateExistingDirectory(
                existingPath,
                allowServiceSid:
                    (currentService.Exists ||
                     previousService.Exists ||
                     allowFreshCreatedDataCleanup) &&
                    (isInstall || isData),
                allowLocalService:
                    isData &&
                    ServiceAccountContract.IsLegacyLocalService(previousService) &&
                    !currentService.Running,
                validateChildren: !SamePath(existingPath, installParent));
        }

        foreach (var transactionPath in transactionPaths)
        {
            var fullPath = Path.GetFullPath(transactionPath);
            var name = Path.GetFileName(fullPath);
            if (!SamePath(Path.GetDirectoryName(fullPath)!, installParent) ||
                !(name.StartsWith("Agent.__staging_", StringComparison.Ordinal) ||
                  name.StartsWith("Agent.__backup_", StringComparison.Ordinal) ||
                  name.StartsWith("Agent.__failed_", StringComparison.Ordinal)))
            {
                throw new SetupException(
                    SetupErrorCodes.RecoveryRequired,
                    "보류 중인 설치 복구 경로가 안전한 제품 경로와 일치하지 않습니다.");
            }

            if (Directory.Exists(fullPath))
            {
                var allowServiceSid =
                    currentService.Exists ||
                    previousService.Exists ||
                    allowFreshCreatedDataCleanup;
                ValidateExistingDirectory(
                    fullPath,
                    allowServiceSid:
                        allowServiceSid &&
                        (name.StartsWith("Agent.__backup_", StringComparison.Ordinal) ||
                         name.StartsWith("Agent.__failed_", StringComparison.Ordinal)),
                    allowLocalService: false);
            }
            else if (File.Exists(fullPath))
            {
                throw new SetupException(
                    SetupErrorCodes.RecoveryRequired,
                    "설치 복구 경로에 예상하지 못한 파일이 있습니다.");
            }
        }
    }

    internal static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateExistingDirectory(
        string path,
        bool allowServiceSid,
        bool allowLocalService,
        bool validateChildren = true)
    {
        var allowedOwners = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.CreatorOwnerSid, null),
            new("S-1-3-4"),
            new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
        };
        if (allowLocalService)
        {
            allowedOwners.Add(new SecurityIdentifier(
                WellKnownSidType.LocalServiceSid,
                null));
        }
        using (var currentIdentity = WindowsIdentity.GetCurrent())
        {
            if (currentIdentity.User is not null &&
                new WindowsPrincipal(currentIdentity)
                    .IsInRole(WindowsBuiltInRole.Administrator))
            {
                allowedOwners.Add(currentIdentity.User);
            }
        }
        if (allowServiceSid)
        {
            allowedOwners.Add(CreateServiceSid(SetupConstants.ServiceName));
        }

        var rootPath = Path.GetFullPath(path);
        var pending = new Queue<string>();
        pending.Enqueue(rootPath);
        var checkedEntries = 0;
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (++checkedEntries > 4096)
            {
                throw new SetupException(
                    SetupErrorCodes.PathUntrusted,
                    "Agent 제품 폴더 항목이 안전 점검 한도를 초과했습니다.");
            }

            var children = InspectExistingPath(
                rootPath,
                current,
                allowedOwners,
                validateChildren);
            if (children is null)
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Enqueue(child);
            }
        }
    }

    private static IReadOnlyList<string>? InspectExistingPath(
        string rootPath,
        string current,
        IReadOnlyCollection<SecurityIdentifier> allowedOwners,
        bool validateChildren)
    {
        try
        {
            return RetryTransientIoOnce(() =>
            {
                var attributes = File.GetAttributes(current);
                if (IsReparsePoint(attributes))
                {
                    throw new SetupException(
                        SetupErrorCodes.PathUntrusted,
                        "Agent 제품 폴더 내부에 재분석 지점 또는 연결 파일이 있어 사용할 수 없습니다.");
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                FileSystemSecurity security = isDirectory
                    ? new DirectoryInfo(current).GetAccessControl(
                        AccessControlSections.Owner | AccessControlSections.Access)
                    : new FileInfo(current).GetAccessControl(
                        AccessControlSections.Owner | AccessControlSections.Access);
                var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ??
                            throw new SetupException(
                                SetupErrorCodes.PathUntrusted,
                                "Agent 제품 폴더 소유자를 확인하지 못했습니다.");
                if (!IsAllowedOwner(owner, allowedOwners) ||
                    HasUntrustedWriteAccess(security, allowedOwners))
                {
                    throw new SetupException(
                        SetupErrorCodes.PathUntrusted,
                        "Agent 제품 폴더 소유권 또는 쓰기 권한이 안전 계약과 일치하지 않습니다.");
                }

                return validateChildren && isDirectory
                    ? Directory.EnumerateFileSystemEntries(current).ToArray()
                    : [];
            });
        }
        catch (Exception exception) when (
            IsVanishedNonRootEntry(rootPath, current, exception))
        {
            EnsureInspectionRootAvailable(rootPath, exception);
            return null;
        }
    }

    internal static void EnsureInspectionRootAvailable(
        string rootPath,
        Exception? innerException = null)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new SetupException(
                SetupErrorCodes.PathNotWritable,
                "Agent 제품 폴더의 상태를 계속 확인할 수 없습니다.",
                innerException);
        }

        var attributes = File.GetAttributes(rootPath);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new SetupException(
                SetupErrorCodes.PathNotWritable,
                "Agent 제품 폴더의 상태를 계속 확인할 수 없습니다.",
                innerException);
        }

        if (IsReparsePoint(attributes))
        {
            throw new SetupException(
                SetupErrorCodes.PathUntrusted,
                "Agent 제품 폴더가 연결 또는 재분석 지점으로 바뀌어 사용할 수 없습니다.",
                innerException);
        }
    }

    internal static T RetryTransientIoOnce<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return action();
        }
        catch (Exception exception) when (IsTransientFileSystemException(exception))
        {
            // A running Agent or endpoint-security product can briefly replace
            // a product file while preflight is inspecting it. Retry once only.
            return action();
        }
    }

    internal static bool IsTransientFileSystemException(Exception exception) =>
        exception is IOException &&
        (exception.HResult & 0xFFFF) is WindowsSharingViolation or WindowsLockViolation;

    internal static bool IsVanishedNonRootEntry(
        string rootPath,
        string current,
        Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException &&
        !SamePath(rootPath, current) &&
        !File.Exists(current) &&
        !Directory.Exists(current);

    internal static SecurityIdentifier CreateServiceSid(string serviceName)
    {
        var bytes = Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant());
        var hash = SHA1.HashData(bytes);
        var parts = Enumerable.Range(0, 5)
            .Select(index => BitConverter.ToUInt32(hash, index * sizeof(uint)));
        return new SecurityIdentifier($"S-1-5-80-{string.Join('-', parts)}");
    }

    internal static bool IsReparsePoint(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    internal static bool IsEmptyNonReparseDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath) ||
                IsReparsePoint(File.GetAttributes(fullPath)))
            {
                return false;
            }

            return !Directory.EnumerateFileSystemEntries(fullPath).Any();
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                NotSupportedException or SecurityException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsAllowedOwner(
        SecurityIdentifier owner,
        IEnumerable<SecurityIdentifier> allowedOwners) =>
        allowedOwners.Contains(owner);

    internal static bool HasUntrustedWriteAccess(
        FileSystemSecurity security,
        IEnumerable<SecurityIdentifier> allowedOwners)
    {
        var allowedWriters = allowedOwners.ToHashSet();
        const FileSystemRights dangerous =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Delete |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        return security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference is SecurityIdentifier sid &&
                !allowedWriters.Contains(sid) &&
                (rule.FileSystemRights & dangerous) != 0);
    }

    private static bool HasCanonicalAllowRule(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity,
        FileSystemRights expectedRights,
        InheritanceFlags expectedInheritance)
    {
        var identityRules = rules
            .Where(rule =>
                rule.IdentityReference is SecurityIdentifier sid &&
                sid.Equals(identity))
            .ToArray();
        return identityRules.Length == 1 &&
               identityRules[0].AccessControlType == AccessControlType.Allow &&
               identityRules[0].FileSystemRights == expectedRights &&
               identityRules[0].InheritanceFlags == expectedInheritance &&
               identityRules[0].PropagationFlags == PropagationFlags.None;
    }

    private static bool HasOwnedAgentData(string dataDirectory)
    {
        var metadata = Path.Combine(dataDirectory, "agent-identity.json");
        var certificate = Path.Combine(dataDirectory, "https-certificate.pfx.dpapi");
        var receipt = Path.Combine(dataDirectory, "install-receipt.json");
        return File.Exists(metadata) && File.Exists(certificate) || File.Exists(receipt);
    }

    private static void AddDirectoryRule(
        DirectorySecurity security,
        IdentityReference identity,
        FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static void AddFileRule(
        FileSecurity security,
        IdentityReference identity,
        FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            AccessControlType.Allow));
}
