using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class AgentPackageValidatorTests
{
    [Fact]
    public void Validate_AcceptsSetupPrimaryAndAgentRuntimeEntries()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var fileSystem = new TestFileSystem();

        var result = new AgentPackageValidator(fileSystem).Validate(package);

        Assert.Equal("0.10.0-poc", result.Version);
        Assert.Equal(
            Path.Combine(package, SetupConstants.AgentExecutableName),
            result.ExecutablePath);
        Assert.Contains(
            result.VerifiedFiles,
            file => file.Name == SetupConstants.SetupExecutableName);
        Assert.Contains(
            result.VerifiedFiles,
            file => file.Name == "agent-companion.dll");
    }

    [Fact]
    public void Validate_RejectsMissingCompanionListedByManifest()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        File.Delete(Path.Combine(package, "agent-companion.dll"));

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.PackageNotFound, exception.Code);
    }

    [Fact]
    public void Validate_RejectsTamperedAgentExecutable()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        File.AppendAllText(
            Path.Combine(package, SetupConstants.AgentExecutableName),
            "tampered");

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.PackageHashMismatch, exception.Code);
    }

    [Fact]
    public void Validate_RejectsAgentAsPrimaryExecutable()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var manifestPath = Path.Combine(package, SetupConstants.ManifestFileName);
        var root = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        root["executable"]!["name"] = SetupConstants.AgentExecutableName;
        File.WriteAllText(
            manifestPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.ManifestInvalid, exception.Code);
    }

    [Fact]
    public void Validate_RejectsCaseInsensitiveDuplicateManifestFileName()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var manifestPath = Path.Combine(package, SetupConstants.ManifestFileName);
        var root = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var files = root["files"]!.AsArray();
        var companion = files.Single(item =>
            string.Equals(
                item!["name"]!.GetValue<string>(),
                "agent-companion.dll",
                StringComparison.Ordinal));
        var duplicate = companion!.DeepClone();
        duplicate!["name"] = "AGENT-COMPANION.DLL";
        files.Add(duplicate);
        File.WriteAllText(
            manifestPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.ManifestInvalid, exception.Code);
    }

    [Fact]
    public void Validate_ResolvesRequiredManifestFileNameCaseInsensitivelyOnWindows()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var manifestPath = Path.Combine(package, SetupConstants.ManifestFileName);
        var root = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var agent = root["files"]!.AsArray().Single(item =>
            string.Equals(
                item!["name"]!.GetValue<string>(),
                SetupConstants.AgentExecutableName,
                StringComparison.Ordinal));
        agent!["name"] = SetupConstants.AgentExecutableName.ToUpperInvariant();
        File.WriteAllText(
            manifestPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var packageResult = new AgentPackageValidator(new TestFileSystem())
            .Validate(package);

        Assert.Contains(packageResult.VerifiedFiles, file => string.Equals(
            file.Name,
            SetupConstants.AgentExecutableName,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsOversizedManifestBeforeDeserialization()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        File.WriteAllText(
            Path.Combine(package, SetupConstants.ManifestFileName),
            new string('x', 2 * 1024 * 1024 + 1));

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.ManifestInvalid, exception.Code);
    }

    [Fact]
    public void Validate_RejectsManifestChangedWhileItIsRead()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var manifestPath = Path.Combine(package, SetupConstants.ManifestFileName);
        var changed = false;
        var fileSystem = new TestFileSystem
        {
            AfterReadAllText = path =>
            {
                if (!changed && string.Equals(
                        path,
                        manifestPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    File.AppendAllText(path, " ");
                }
            }
        };

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(fileSystem).Validate(package));

        Assert.Equal(SetupErrorCodes.ManifestInvalid, exception.Code);
    }

    [Fact]
    public void Validate_WhenManifestGrowsAfterLengthCheck_BoundedReadRejectsIt()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var manifestPath = Path.Combine(package, SetupConstants.ManifestFileName);
        var growthInjected = false;
        var fileSystem = new TestFileSystem
        {
            BeforeReadAllTextBounded = path =>
            {
                if (!growthInjected && string.Equals(
                        path,
                        manifestPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    growthInjected = true;
                    File.WriteAllText(path, new string('x', 2 * 1024 * 1024 + 1));
                }
            }
        };

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(fileSystem).Validate(package));

        Assert.True(growthInjected);
        Assert.Equal(SetupErrorCodes.ManifestInvalid, exception.Code);
    }

    [Fact]
    public void Validate_RejectsInvalidUtf8InsideOtherwiseValidManifestString()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var manifestPath = Path.Combine(package, SetupConstants.ManifestFileName);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var versionBytes = Encoding.UTF8.GetBytes("0.10.0-poc");
        var versionOffset = manifestBytes.AsSpan().IndexOf(versionBytes);
        Assert.True(versionOffset >= 0);
        manifestBytes[versionOffset + 1] = 0xff;
        File.WriteAllBytes(manifestPath, manifestBytes);

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.ManifestInvalid, exception.Code);
    }

    [Fact]
    public void Validate_RejectsManifestSizeMismatchEvenWhenHashMatches()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var manifestPath = Path.Combine(package, SetupConstants.ManifestFileName);
        var root = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var companion = root["files"]!.AsArray().Single(item =>
            string.Equals(
                item!["name"]!.GetValue<string>(),
                "agent-companion.dll",
                StringComparison.Ordinal));
        companion!["size"] = companion["size"]!.GetValue<long>() + 1;
        File.WriteAllText(manifestPath, root.ToJsonString());

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.PackageHashMismatch, exception.Code);
    }

    [Fact]
    public void Validate_RejectsUnlistedTopLevelFile()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        File.WriteAllText(Path.Combine(package, "unlisted.txt"), "unexpected");

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.ManifestInvalid, exception.Code);
    }

    [Fact]
    public void Validate_RejectsUnlistedTopLevelDirectory()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        Directory.CreateDirectory(Path.Combine(package, "unexpected-directory"));

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.ManifestInvalid, exception.Code);
    }
}
