using System.Security.Cryptography;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Api;

public sealed class BearerAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        AgentAuthenticationMaterial authentication)
    {
        if (!IsAuthorized(context.Request.Headers.Authorization, authentication))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = AgentErrorCodes.AuthenticationRequired,
                    message = "Agent authentication is required."
                }
            }, cancellationToken: context.RequestAborted);
            return;
        }

        await next(context);
    }

    internal static bool IsAuthorized(
        string? authorization,
        AgentAuthenticationMaterial authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        if (authorization is null
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || authorization.Length != 50)
        {
            return false;
        }

        Span<byte> candidate = stackalloc byte[AgentAuthenticationMaterial.TokenLength];
        try
        {
            return Base64Url.TryDecode32(authorization[7..], candidate)
                && authentication.FixedTimeEquals(candidate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }
}
