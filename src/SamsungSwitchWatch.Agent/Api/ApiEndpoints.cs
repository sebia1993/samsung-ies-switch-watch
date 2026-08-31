using System.Diagnostics;
using System.Reflection;
using SamsungSwitchWatch.Agent.Diagnostics;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Execution;
using SamsungSwitchWatch.Agent.Security;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Profiles;

namespace SamsungSwitchWatch.Agent.Api;

public static class ApiEndpoints
{
    private static readonly string ProductVersion = ResolveProductVersion();

    public static void MapAgentEndpoints(this WebApplication app, AgentOptions options)
    {
        app.MapGet("/health/live", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                status = "live",
                utc = DateTimeOffset.UtcNow
            });
        });

        app.MapGet("/health/ready", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                status = "ready",
                apiVersion = 5,
                productVersion = ProductVersion,
                protocol = "https",
                utc = DateTimeOffset.UtcNow
            });
        });

        app.MapGet("/api/v5/identity", (AgentIdentity identity) => Results.Ok(new
        {
            apiVersion = 5,
            productVersion = ProductVersion,
            agentId = options.AgentId,
            instanceId = identity.InstanceId,
            certificatePublicKeySha256 = identity.CertificatePublicKeySha256,
            protocol = "https",
            maxCommandsPerRequest = options.MaxCommandsPerRequest,
            maxOutputBytes = options.MaxOutputBytes
        }));

        app.MapPost("/api/v5/telnet/test", (
            TelnetApiRequest request,
            HttpContext context,
            TargetNetworkPolicy targetPolicy,
            DeviceProfileRegistry profiles,
            TelnetExecutionAdmission admission,
            IStatelessTelnetExecutor executor,
            CancellationToken cancellationToken) =>
            ExecuteAsync(
                request,
                isTest: true,
                context,
                targetPolicy,
                profiles,
                options,
                admission,
                executor,
                cancellationToken));

        app.MapPost("/api/v5/telnet/execute", (
            TelnetApiRequest request,
            HttpContext context,
            TargetNetworkPolicy targetPolicy,
            DeviceProfileRegistry profiles,
            TelnetExecutionAdmission admission,
            IStatelessTelnetExecutor executor,
            CancellationToken cancellationToken) =>
            ExecuteAsync(
                request,
                isTest: false,
                context,
                targetPolicy,
                profiles,
                options,
                admission,
                executor,
                cancellationToken));

        app.MapMethods("/api/v4", AllHttpMethods, UpgradeRequired);
        app.MapMethods("/api/v4/{**remainder}", AllHttpMethods, UpgradeRequired);
    }

    private static readonly string[] AllHttpMethods =
    [
        HttpMethods.Get,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
        HttpMethods.Options,
        HttpMethods.Head
    ];

    private static IResult UpgradeRequired(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(
            new
            {
                error = new
                {
                    code = AgentErrorCodes.ApiUpgradeRequired,
                    message = "Agent API v5 and a new pairing code are required."
                }
            },
            statusCode: StatusCodes.Status426UpgradeRequired);
    }

    private static string ResolveProductVersion()
    {
        var value = typeof(ApiEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(value))
        {
            return typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var metadata = value.IndexOf('+');
        return metadata >= 0 ? value[..metadata] : value;
    }

    private static async Task<IResult> ExecuteAsync(
        TelnetApiRequest request,
        bool isTest,
        HttpContext context,
        TargetNetworkPolicy targetPolicy,
        DeviceProfileRegistry profiles,
        AgentOptions options,
        TelnetExecutionAdmission admission,
        IStatelessTelnetExecutor executor,
        CancellationToken cancellationToken)
    {
        AgentRuntimeDiagnostics.RecordRequestStarted();
        var startedTimestamp = Stopwatch.GetTimestamp();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        try
        {
            var validated = TelnetRequestValidator.Validate(
                request,
                isTest,
                targetPolicy,
                profiles,
                options);
            var clientAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            await using var lease = await admission.EnterAsync(
                clientAddress,
                validated.Address,
                cancellationToken);
            using var activeSession = AgentRuntimeDiagnostics.BeginActiveSession();
            var result = await executor.ExecuteAsync(validated, cancellationToken);
            AgentRuntimeDiagnostics.RecordReconnects(result.ReconnectCount);
            return Results.Ok(result);
        }
        catch (SwitchWatchException exception)
        {
            AgentRuntimeDiagnostics.RecordRequestFailed(exception.Error.Code);
            throw TelnetFailureMapper.Map(exception);
        }
        catch (AgentOperationException exception)
        {
            AgentRuntimeDiagnostics.RecordRequestFailed(exception.Code);
            throw;
        }
        catch
        {
            AgentRuntimeDiagnostics.RecordRequestFailed(AgentErrorCodes.InternalError);
            throw;
        }
        finally
        {
            AgentRuntimeDiagnostics.RecordDuration(
                Stopwatch.GetElapsedTime(startedTimestamp));
        }
    }
}

public sealed class ErrorHandlingMiddleware(
    RequestDelegate next,
    ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var startedTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The Viewer disconnected or cancelled. Do not attempt to write a
            // response and do not log request data.
        }
        catch (AgentOperationException exception)
        {
            context.Response.StatusCode = exception.StatusCode;
            if (exception.Details is null)
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new { code = exception.Code, message = exception.SafeMessage }
                }, cancellationToken: context.RequestAborted);
                return;
            }

            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = exception.Code,
                    message = exception.SafeMessage,
                    details = exception.Details
                }
            }, cancellationToken: context.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = AgentErrorCodes.RequestInvalid,
                    message = "Request body is invalid."
                }
            }, cancellationToken: context.RequestAborted);
        }
        catch (Exception)
        {
            var durationMs = (long)Math.Max(
                0,
                Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
            logger.LogError(
                "Agent request failed. Stage={Stage} Code={Code} CorrelationId={CorrelationId} DurationMs={DurationMs}.",
                SafeStage(context.Request.Path),
                AgentErrorCodes.InternalError,
                SafeCorrelationId(context.TraceIdentifier),
                durationMs);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = AgentErrorCodes.InternalError,
                    message = "Agent request failed.",
                    correlationId = SafeCorrelationId(context.TraceIdentifier)
                }
            }, cancellationToken: context.RequestAborted);
        }
    }

    private static string SafeStage(PathString path) =>
        path.Value?.ToLowerInvariant() switch
        {
            "/api/v5/telnet/test" => "telnet-test",
            "/api/v5/telnet/execute" => "telnet-execute",
            "/api/v5/identity" => "identity",
            "/health/live" => "health-live",
            "/health/ready" => "health-ready",
            _ => "http-request"
        };

    private static string SafeCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return "unavailable";
        }

        return value.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            ? value
            : "unavailable";
    }
}
