using System.Text.Json.Nodes;

namespace SamsungSwitchWatch.Agent.Setup.Deployment;

internal sealed record ExistingTargetNetworksLoadResult(
    IReadOnlyList<string> TargetCidrs,
    SetupStepResult? Warning)
{
    public bool Succeeded => Warning is null;
}

internal sealed class ExistingTargetNetworkLoader(
    ISetupFileSystem fileSystem,
    DeploymentPaths paths)
{
    private const int MaximumConfigurationBytes = 64 * 1024;

    public ExistingTargetNetworksLoadResult Load()
    {
        try
        {
            if (!fileSystem.FileExists(paths.ProductionConfigurationPath))
            {
                return Success([]);
            }

            if (JsonNode.Parse(
                    fileSystem.ReadAllTextBounded(
                        paths.ProductionConfigurationPath,
                        MaximumConfigurationBytes)) is not
                JsonObject root ||
                root["Agent"] is not JsonObject agent ||
                agent["AllowedTargetCidrs"] is not JsonArray values ||
                values.Count > SetupConstants.MaximumTargetCidrs)
            {
                return Warning();
            }

            if (values.Count == 0)
            {
                return Success([]);
            }

            var cidrs = new List<string>(values.Count);
            foreach (var value in values)
            {
                if (value is not JsonValue jsonValue ||
                    !jsonValue.TryGetValue<string>(out var cidr))
                {
                    return Warning();
                }

                cidrs.Add(cidr);
            }

            return Success(SetupTargetCidrPolicy.Normalize(cidrs));
        }
        catch
        {
            return Warning();
        }
    }

    private static ExistingTargetNetworksLoadResult Success(
        IReadOnlyList<string> targetCidrs) =>
        new(targetCidrs, null);

    private static ExistingTargetNetworksLoadResult Warning() =>
        new(
            [],
            new SetupStepResult(
                SetupErrorCodes.ExistingNetworksNotLoaded,
                "기존 관리망 설정",
                SetupStepState.Warning,
                "기존 Agent 관리망을 불러오지 못했습니다. 승인된 관리망을 다시 선택하거나 추가하세요."));
}
