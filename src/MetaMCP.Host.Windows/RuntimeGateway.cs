using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace MetaMCP.Host;

internal sealed record RuntimeTarget(string Slot, int BackendPort, int FrontendPort);

internal sealed record RuntimeGatewaySlotSnapshot(
    string Slot,
    int BackendPort,
    int FrontendPort,
    int Sessions,
    int InFlightRequests,
    bool Active);

internal sealed record RuntimeGatewaySessionRoute(
    RuntimeTarget Target,
    DateTimeOffset LastActivityAt);

internal sealed class RuntimeGateway : IAsyncDisposable
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
    };

    private readonly int _publicBackendPort;
    private readonly int _publicFrontendPort;
    private readonly string _controlToken;
    private readonly Func<object> _statusProvider;
    private readonly Func<string?, CancellationToken, Task<object>> _swapHandler;
    private readonly TimeSpan _sessionIdleTimeout;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, RuntimeGatewaySessionRoute> _sessionTargets =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _inFlightBySlot =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _activeSync = new();
    private RuntimeTarget _activeTarget;
    private WebApplication? _app;

    public RuntimeGateway(
        int publicBackendPort,
        int publicFrontendPort,
        string controlToken,
        RuntimeTarget initialTarget,
        TimeSpan sessionIdleTimeout,
        Func<object> statusProvider,
        Func<string?, CancellationToken, Task<object>> swapHandler)
    {
        _publicBackendPort = publicBackendPort;
        _publicFrontendPort = publicFrontendPort;
        _controlToken = controlToken;
        _activeTarget = initialTarget;
        _sessionIdleTimeout = sessionIdleTimeout;
        _statusProvider = statusProvider;
        _swapHandler = swapHandler;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public RuntimeTarget ActiveTarget
    {
        get
        {
            lock (_activeSync)
            {
                return _activeTarget;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, _publicFrontendPort);
            options.Listen(IPAddress.Loopback, _publicBackendPort);
        });

        var app = builder.Build();
        app.Run(HandleRequestAsync);
        await app.StartAsync(cancellationToken);
        _app = app;
        HostLog.Info(
            $"Runtime gateway listening on frontend {_publicFrontendPort} and backend {_publicBackendPort}.");
    }

    public void SwitchActive(RuntimeTarget target)
    {
        lock (_activeSync)
        {
            _activeTarget = target;
        }
        HostLog.Info(
            $"Runtime gateway active target -> {target.Slot} ({target.FrontendPort}/{target.BackendPort}).");
    }

    public RuntimeGatewaySlotSnapshot GetSlotSnapshot(RuntimeTarget target)
    {
        PruneExpiredSessions();
        var sessions = _sessionTargets.Count(pair =>
            pair.Value.Target.Slot.Equals(target.Slot, StringComparison.OrdinalIgnoreCase));
        var inFlight = _inFlightBySlot.TryGetValue(target.Slot, out var count) ? count : 0;
        return new RuntimeGatewaySlotSnapshot(
            target.Slot,
            target.BackendPort,
            target.FrontendPort,
            sessions,
            inFlight,
            ActiveTarget.Slot.Equals(target.Slot, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsDrained(RuntimeTarget target)
    {
        var snapshot = GetSlotSnapshot(target);
        return snapshot.Sessions == 0 && snapshot.InFlightRequests == 0;
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        if (context.Connection.LocalPort == _publicBackendPort &&
            context.Request.Path.StartsWithSegments("/__host/runtime"))
        {
            await HandleControlAsync(context);
            return;
        }

        var backendRequest = context.Connection.LocalPort == _publicBackendPort;
        var sessionId = backendRequest
            ? context.Request.Headers["mcp-session-id"].FirstOrDefault()
                ?? context.Request.Query["sessionId"].FirstOrDefault()
            : null;
        var target = ResolveTarget(sessionId);
        var targetPort = backendRequest ? target.BackendPort : target.FrontendPort;

        _inFlightBySlot.AddOrUpdate(target.Slot, 1, (_, current) => current + 1);
        try
        {
            await ProxyAsync(context, target, targetPort, sessionId, backendRequest);
        }
        finally
        {
            _inFlightBySlot.AddOrUpdate(target.Slot, 0, (_, current) => Math.Max(0, current - 1));
        }
    }

    private RuntimeTarget ResolveTarget(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            _sessionTargets.TryGetValue(sessionId, out var pinned))
        {
            var now = DateTimeOffset.UtcNow;
            if (now - pinned.LastActivityAt <= _sessionIdleTimeout)
            {
                _sessionTargets[sessionId] = pinned with { LastActivityAt = now };
                return pinned.Target;
            }

            _sessionTargets.TryRemove(sessionId, out _);
        }

        return ActiveTarget;
    }

    private void PinSession(string sessionId, RuntimeTarget target)
    {
        _sessionTargets[sessionId] = new RuntimeGatewaySessionRoute(
            target,
            DateTimeOffset.UtcNow);
    }

    private void PruneExpiredSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - _sessionIdleTimeout;
        foreach (var pair in _sessionTargets)
        {
            if (pair.Value.LastActivityAt < cutoff)
            {
                _sessionTargets.TryRemove(pair.Key, out _);
            }
        }
    }

    private async Task ProxyAsync(
        HttpContext context,
        RuntimeTarget target,
        int targetPort,
        string? requestSessionId,
        bool backendRequest)
    {
        var uri = new UriBuilder(
            Uri.UriSchemeHttp,
            "127.0.0.1",
            targetPort,
            context.Request.Path,
            context.Request.QueryString.Value ?? string.Empty).Uri;

        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            uri);

        var hasBody = context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding");
        if (hasBody)
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) &&
                request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        request.Headers.Host = $"127.0.0.1:{targetPort}";
        if (!request.Headers.Contains("X-Forwarded-Host") &&
            context.Request.Headers.Host.Count > 0)
        {
            request.Headers.TryAddWithoutValidation(
                "X-Forwarded-Host",
                context.Request.Headers.Host.ToString());
        }
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);

        context.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response.Headers, context.Response.Headers);
        CopyResponseHeaders(response.Content.Headers, context.Response.Headers);
        RewriteInternalRedirects(context, targetPort);
        context.Response.Headers.Remove("transfer-encoding");

        if (backendRequest &&
            response.Headers.TryGetValues("mcp-session-id", out var values))
        {
            var responseSessionId = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(responseSessionId))
            {
                PinSession(responseSessionId, target);
            }
        }

        var isSse = backendRequest &&
            context.Request.Path.Value?.EndsWith("/sse", StringComparison.OrdinalIgnoreCase) == true &&
            response.Content.Headers.ContentType?.MediaType?.Equals(
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase) == true;
        if (isSse)
        {
            await CopySseAndCaptureSessionAsync(
                response,
                context.Response.Body,
                target,
                context.RequestAborted);
        }
        else
        {
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }

        if (backendRequest &&
            HttpMethods.IsDelete(context.Request.Method) &&
            !string.IsNullOrWhiteSpace(requestSessionId) &&
            response.IsSuccessStatusCode)
        {
            _sessionTargets.TryRemove(requestSessionId, out _);
        }
    }

    private async Task CopySseAndCaptureSessionAsync(
        HttpResponseMessage response,
        Stream destination,
        RuntimeTarget target,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[4096];
        var prefix = new StringBuilder(8192);
        string? capturedSessionId = null;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (capturedSessionId is null && prefix.Length < 16384)
                {
                    prefix.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    var match = Regex.Match(
                        prefix.ToString(),
                        @"[?&]sessionId=([^&\r\n\s]+)",
                        RegexOptions.CultureInvariant);
                    if (match.Success)
                    {
                        var sessionId = Uri.UnescapeDataString(match.Groups[1].Value);
                        if (!string.IsNullOrWhiteSpace(sessionId))
                        {
                            PinSession(sessionId, target);
                            capturedSessionId = sessionId;
                        }
                    }
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(capturedSessionId))
            {
                _sessionTargets.TryRemove(capturedSessionId, out _);
            }
        }
    }
    private async Task HandleControlAsync(HttpContext context)
    {
        if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) ||
            !StringValues.Equals(
                context.Request.Headers["X-MetaMCP-Host-Control-Token"],
                _controlToken))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            if (HttpMethods.IsGet(context.Request.Method) &&
                context.Request.Path.Equals("/__host/runtime/status"))
            {
                await context.Response.WriteAsJsonAsync(
                    _statusProvider(),
                    cancellationToken: context.RequestAborted);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) &&
                context.Request.Path.Equals("/__host/runtime/swap"))
            {
                string? slot = null;
                if (context.Request.ContentLength is > 0)
                {
                    var payload = await JsonSerializer.DeserializeAsync<RuntimeSwapRequest>(
                        context.Request.Body,
                        cancellationToken: context.RequestAborted);
                    slot = payload?.Slot;
                }

                var result = await _swapHandler(slot, context.RequestAborted);
                await context.Response.WriteAsJsonAsync(
                    result,
                    cancellationToken: context.RequestAborted);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (InvalidOperationException ex)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(
                new { error = ex.Message },
                cancellationToken: context.RequestAborted);
        }
        catch (Exception ex)
        {
            HostLog.Error("Runtime control request failed.", ex);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new { error = ex.Message },
                cancellationToken: context.RequestAborted);
        }
    }

    private static void CopyResponseHeaders(
        HttpHeaders source,
        IHeaderDictionary target)
    {
        foreach (var header in source)
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                target[header.Key] = new StringValues(header.Value.ToArray());
            }
        }
    }

    private static void RewriteInternalRedirects(HttpContext context, int targetPort)
    {
        var locations = context.Response.Headers["Location"];
        if (locations.Count == 0 || !context.Request.Host.HasValue)
        {
            return;
        }

        var requestHost = context.Request.Host;
        var rewritten = new string[locations.Count];
        var changed = false;
        for (var index = 0; index < locations.Count; index++)
        {
            var value = locations[index] ?? string.Empty;
            if (Uri.TryCreate(value, UriKind.Absolute, out var location) &&
                location.IsLoopback &&
                location.Port == targetPort)
            {
                var publicLocation = new UriBuilder(location)
                {
                    Scheme = context.Request.Scheme,
                    Host = requestHost.Host,
                    Port = requestHost.Port ?? context.Connection.LocalPort,
                };
                rewritten[index] = publicLocation.Uri.ToString();
                changed = true;
            }
            else
            {
                rewritten[index] = value;
            }
        }

        if (changed)
        {
            context.Response.Headers["Location"] = new StringValues(rewritten);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _app.StopAsync(timeout.Token);
            }
            catch
            {
            }
            await _app.DisposeAsync();
            _app = null;
        }
        _http.Dispose();
    }

    private sealed record RuntimeSwapRequest(string? Slot);
}
