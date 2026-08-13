using System.Text;
using System.Text.Json;

namespace SamsungSwitchWatch.Agent.Setup.Deployment;

public sealed class AgentPackageValidator(ISetupFileSystem fileSystem) : IAgentPackageValidator
{
    private const int MaximumManifestBytes = 2 * 1024 * 1024;

    public AgentPackage Validate(string packageDirectory)
    {
        var packageRoot = Path.GetFullPath(packageDirectory);
        var agentExecutablePath = Path.Combine(packageRoot, SetupConstants.AgentExecutableName);
        var setupExecutablePath = Path.Combine(packageRoot, SetupConstants.SetupExecutableName);
        var manifestPath = Path.Combine(packageRoot, SetupConstants.ManifestFileName);

        if (!fileSystem.FileExists(agentExecutablePath) ||
            !fileSystem.FileExists(setupExecutablePath) ||
            !fileSystem.FileExists(manifestPath))
        {
            throw new SetupException(
                SetupErrorCodes.PackageNotFound,
                "Agent 실행 파일 또는 BUILD-MANIFEST.json이 Setup 옆에 없습니다.");
        }

        BuildManifest? manifest;
        string manifestSha256;
        try
        {
            if (fileSystem.GetFileLength(manifestPath) > MaximumManifestBytes)
            {
                throw new JsonException();
            }

            var beforeReadHash = fileSystem.ComputeSha256(manifestPath);
            var json = fileSystem.ReadAllTextBounded(
                manifestPath,
                MaximumManifestBytes);
            manifestSha256 = fileSystem.ComputeSha256(manifestPath);
            if (!string.Equals(
                    beforeReadHash,
                    manifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The package manifest changed while it was read.");
            }

            if (json.Length > MaximumManifestBytes)
            {
                throw new JsonException();
            }

            manifest = JsonSerializer.Deserialize<BuildManifest>(json, JsonOptions);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or DecoderFallbackException)
        {
            throw new SetupException(
                SetupErrorCodes.ManifestInvalid,
                "BUILD-MANIFEST.json 형식을 확인할 수 없습니다.",
                exception);
        }

        if (manifest is null ||
            manifest.ManifestVersion != 1 ||
            !string.Equals(manifest.Product, SetupConstants.ProductName, StringComparison.Ordinal) ||
            !string.Equals(manifest.PackageKind, "Agent", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            manifest.SourceCommit?.Length != 40 ||
            manifest.SourceCommit.Any(character => !Uri.IsHexDigit(character)) ||
            manifest.Executable is null ||
            !string.Equals(
                manifest.Executable.Name,
                SetupConstants.SetupExecutableName,
                StringComparison.Ordinal) ||
            !IsSha256(manifest.Executable.Sha256) ||
            manifest.Files is null ||
            manifest.Files.Count == 0)
        {
            throw new SetupException(
                SetupErrorCodes.ManifestInvalid,
                "Agent 빌드 정보가 누락되었거나 지원하지 않는 형식입니다.");
        }

        var verifiedFiles = new List<PackageFile>();
        // NTFS/Windows resolves package file names case-insensitively. Reject
        // aliases such as companion.dll and COMPANION.DLL before either name
        // can be treated as a distinct manifest entry for the same file.
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.Name) ||
                Path.GetFileName(entry.Name) != entry.Name ||
                !seenNames.Add(entry.Name) ||
                !IsSha256(entry.Sha256) ||
                entry.Size < 0)
            {
                throw new SetupException(
                    SetupErrorCodes.ManifestInvalid,
                    "Agent 패키지 파일 목록이 안전하지 않거나 중복되어 있습니다.");
            }

            var filePath = Path.Combine(packageRoot, entry.Name);
            if (!fileSystem.FileExists(filePath))
            {
                throw new SetupException(
                    SetupErrorCodes.PackageNotFound,
                    $"Agent 패키지 필수 파일이 없습니다: {entry.Name}");
            }

            var fileHash = fileSystem.ComputeSha256(filePath);
            if (fileSystem.GetFileLength(filePath) != entry.Size ||
                !string.Equals(fileHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException(
                    SetupErrorCodes.PackageHashMismatch,
                    $"Agent 패키지 파일 무결성 확인에 실패했습니다: {entry.Name}");
            }

            verifiedFiles.Add(new PackageFile(
                entry.Name,
                filePath,
                entry.Sha256!.ToLowerInvariant(),
                entry.Size));
        }

        var actualNames = fileSystem
            .EnumerateTopLevelFiles(packageRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedNames = seenNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        expectedNames.Add(SetupConstants.ManifestFileName);
        if (!actualNames.SetEquals(expectedNames) ||
            fileSystem.EnumerateTopLevelDirectories(packageRoot).Count != 0)
        {
            throw new SetupException(
                SetupErrorCodes.ManifestInvalid,
                "Agent 패키지 파일 목록이 빌드 목록과 일치하지 않습니다.");
        }

        var setupFile = verifiedFiles.SingleOrDefault(file =>
            string.Equals(
                file.Name,
                SetupConstants.SetupExecutableName,
                StringComparison.OrdinalIgnoreCase));
        var agentFile = verifiedFiles.SingleOrDefault(file =>
            string.Equals(
                file.Name,
                SetupConstants.AgentExecutableName,
                StringComparison.OrdinalIgnoreCase));
        if (setupFile is null || agentFile is null)
        {
            throw new SetupException(
                SetupErrorCodes.ManifestInvalid,
                "Agent 또는 Setup 실행 파일이 패키지 파일 목록에 없습니다.");
        }

        var actualHash = fileSystem.ComputeSha256(setupExecutablePath);
        if (!string.Equals(
                actualHash,
                manifest.Executable.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupException(
                SetupErrorCodes.PackageHashMismatch,
                "Setup 실행 파일이 기본 빌드 정보와 일치하지 않습니다. 원본 ZIP을 다시 준비하세요.");
        }

        return new AgentPackage(
            manifest.Version,
            manifest.SourceCommit!,
            agentExecutablePath,
            manifestPath,
            agentFile.Sha256,
            verifiedFiles)
        {
            ManifestSha256 = manifestSha256
        };
    }

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class BuildManifest
    {
        public int ManifestVersion { get; init; }
        public string? Product { get; init; }
        public string? PackageKind { get; init; }
        public string? Version { get; init; }
        public string? SourceCommit { get; init; }
        public ExecutableManifest? Executable { get; init; }
        public List<FileManifest?>? Files { get; init; }
    }

    private sealed class ExecutableManifest
    {
        public string? Name { get; init; }
        public string? Sha256 { get; init; }
    }

    private sealed class FileManifest
    {
        public string? Name { get; init; }
        public string? Sha256 { get; init; }
        public long Size { get; init; }
    }
}
