namespace NavalArchive.Api.Middleware;

/// <summary>
/// Ensures all requests have a valid session. Blocks requests from blocked IPs.
/// Session is created on first visit; subsequent API calls require the session cookie.
/// </summary>
public class SessionGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private static readonly HashSet<string> BlockedIPs = new(StringComparer.OrdinalIgnoreCase);

    public SessionGateMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _config = config;
        LoadBlocklist();
    }

    private void LoadBlocklist()
    {
        var section = _config.GetSection("SessionGate:BlockedIPs");
        if (section.Exists())
        {
            foreach (var ip in section.Get<string[]>() ?? [])
            {
                if (!string.IsNullOrWhiteSpace(ip))
                    BlockedIPs.Add(ip.Trim());
            }
        }
    }

    public static void AddBlockedIP(string ip)
    {
        if (!string.IsNullOrWhiteSpace(ip))
            BlockedIPs.Add(ip.Trim());
    }

    public static IReadOnlyCollection<string> GetBlockedIPs() => BlockedIPs.ToList();

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = GetClientIp(context);
        if (!string.IsNullOrEmpty(ip) && BlockedIPs.Contains(ip))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Access denied", code = "blocked" });
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var isPublic = path.Equals("/health", StringComparison.OrdinalIgnoreCase);

        if (isPublic)
        {
            await _next(context);
            return;
        }

        var requireSession = _config.GetValue<bool>("SessionGate:RequireSessionForApi");
        var sessionCookieName = _config["SessionGate:SessionCookieName"] ?? ".AspNetCore.Session";
        var hasSessionCookie = context.Request.Cookies.ContainsKey(sessionCookieName);

        if (requireSession && path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            // API calls must have session cookie (obtained by visiting the website first)
            if (!hasSessionCookie)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Session required. Visit the website first to obtain a session.",
                    code = "session_required"
                });
                return;
            }
        }
        else
        {
            // Non-API or when not requiring session: ensure session exists (triggers creation on first visit)
            await context.Session.CommitAsync(context.RequestAborted);
        }

        await _next(context);
    }

    private static string? GetClientIp(HttpContext context)
    {
        // Respect X-Forwarded-For when behind proxy/load balancer
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first))
                return first;
        }
        return context.Connection.RemoteIpAddress?.ToString();
    }
}
