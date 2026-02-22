using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MultiFunPlayer.Common;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.Remote;
using NLog;

namespace MultiFunPlayer.Http;

public sealed class HttpActionService : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly IShortcutActionRunner _actionRunner;
    private readonly IShortcutManager _shortcutManager;
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly string _host;
    private readonly int _port;
    private readonly string _prefixPath;
    private readonly bool _enabled;
    private readonly bool _debug;
    private readonly bool _authEnabled;
    private readonly string _username;
    private readonly string _password;

    private CancellationTokenSource _cts;
    private Task _serverTask;

    public bool IsRunning => _listener.IsListening;
    public string Prefix => $"http://{_host}:{_port}{_prefixPath}";

    public HttpActionService(IShortcutActionRunner actionRunner, IShortcutManager shortcutManager, HttpConfig config)
    {
        _actionRunner = actionRunner;
        _shortcutManager = shortcutManager;
        _enabled = config.Enabled;
        _host = string.IsNullOrWhiteSpace(config.Host) ? "127.0.0.1" : config.Host;
        _port = config.Port <= 0 ? 53123 : config.Port;
        _prefixPath = NormalizePrefixPath(config.PrefixPath);
        _debug = config.Debug;
        _authEnabled = config.AuthEnabled;
        _username = config.Username ?? string.Empty;
        _password = config.Password ?? string.Empty;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            _logger.Info("HttpActionService disabled by config");
            return Task.CompletedTask;
        }

        if (_listener.IsListening)
            return Task.CompletedTask;

        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(Prefix);
        _listener.Start();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _serverTask = Task.Run(() => RunServerAsync(_cts.Token), _cts.Token);

        _logger.Info("HttpActionService listening on {Prefix}", Prefix);
        if (_debug) RemotePipelineLogger.Log("HTTP-API", $"Listening on {Prefix}");
        return Task.CompletedTask;
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;
            var route = request.Url?.AbsolutePath ?? "/";
            var method = request.HttpMethod?.ToUpperInvariant() ?? "GET";
            if (_debug) RemotePipelineLogger.Log("HTTP-API", $"{method} {route} from {context.Request.RemoteEndPoint}");

            if (RequiresAuth() && !IsAuthorized(context.Request))
            {
                context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"MultiFunPlayer\"";
                await WriteJsonAsync(context.Response, 401, new { error = "Unauthorized" });
                return;
            }

            if (method == "GET" && route.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, 200, new { ok = true, service = "MultiFunPlayer.HttpActionService" });
                return;
            }

            if (method == "GET" && route.EndsWith("/actions", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, 200, new { actions = _shortcutManager.AvailableActions.OrderBy(x => x).ToArray() });
                return;
            }

            if (method == "POST" && route.EndsWith("/actions/invoke", StringComparison.OrdinalIgnoreCase))
            {
                await HandleInvokeAsync(context, cancellationToken);
                return;
            }

            await WriteJsonAsync(context.Response, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unhandled exception in HttpActionService");
            await WriteJsonAsync(context.Response, 500, new { error = ex.Message });
        }
    }

    private async Task HandleInvokeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
        var payload = await reader.ReadToEndAsync(cancellationToken);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context.Response, 400, new { error = $"Invalid JSON payload: {ex.Message}" });
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("Action", out var actionElement))
            {
                await WriteJsonAsync(context.Response, 400, new { error = "Missing Action field" });
                return;
            }

            var actionName = actionElement.GetString();
            var resolvedName = ResolveBestMatchingAction(actionName);
            if (resolvedName == null)
            {
                await WriteJsonAsync(context.Response, 404, new { error = $"Unknown action: {actionName}" });
                return;
            }

            var action = _shortcutManager.GetAction(resolvedName);
            var invokeMethods = action.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name.StartsWith("Invoke") && m.ReturnType == typeof(ValueTask))
                .ToList();

            var argElements = root.TryGetProperty("Arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
                ? argsElement.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();

            MethodInfo invokeMethod = null;
            foreach (var method in invokeMethods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != argElements.Length)
                    continue;

                var allMatch = true;
                for (var i = 0; i < parameters.Length; i++)
                {
                    try
                    {
                        ConvertJsonToType(argElements[i].GetProperty("Value"), parameters[i].ParameterType);
                    }
                    catch
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                {
                    invokeMethod = method;
                    break;
                }
            }

            if (invokeMethod == null)
            {
                await WriteJsonAsync(context.Response, 400, new { error = $"No matching Invoke overload found for action {resolvedName}" });
                return;
            }

            var expectedTypes = invokeMethod.GetParameters().Select(p => p.ParameterType).ToArray();
            var args = new object[expectedTypes.Length];
            for (var i = 0; i < expectedTypes.Length; i++)
                args[i] = ConvertJsonToType(argElements[i].GetProperty("Value"), expectedTypes[i]);

            var success = await _actionRunner.TryInvokeWithFeedbackAsync(resolvedName, args);
            if (!success)
            {
                await WriteJsonAsync(context.Response, 400, new { ok = false, action = resolvedName, error = "Invocation failed" });
                return;
            }

            await WriteJsonAsync(context.Response, 200, new { ok = true, action = resolvedName });
            if (_debug) RemotePipelineLogger.Log("HTTP-API", $"Invoked {resolvedName} ({args.Length} args)");
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private object ConvertJsonToType(JsonElement element, Type targetType)
    {
        if (targetType == typeof(DeviceAxis))
            return ParseDeviceAxis(element);
        if (targetType.IsEnum)
            return Enum.Parse(targetType, element.GetString(), ignoreCase: true);
        if (targetType == typeof(Guid))
            return element.GetGuid();
        if (targetType == typeof(string))
            return element.GetString();
        if (targetType == typeof(int))
            return element.GetInt32();
        if (targetType == typeof(double))
            return element.GetDouble();
        if (targetType == typeof(float))
            return (float)element.GetDouble();
        if (targetType == typeof(decimal))
            return element.GetDecimal();
        if (targetType == typeof(bool))
            return element.GetBoolean();

        return JsonSerializer.Deserialize(element.GetRawText(), targetType);
    }

    private static DeviceAxis ParseDeviceAxis(JsonElement valueElement)
    {
        var name = valueElement.GetString();
        var axis = DeviceAxis.Parse(name);
        if (axis == null)
            throw new ArgumentException($"Invalid DeviceAxis name: '{name}'");
        return axis;
    }

    private string ResolveBestMatchingAction(string requestedName)
    {
        var available = _shortcutManager.AvailableActions;
        if (available.Contains(requestedName))
            return requestedName;

        var matches = available.Where(a => a.EndsWith(requestedName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private bool RequiresAuth() => _authEnabled && (!string.IsNullOrWhiteSpace(_username) || !string.IsNullOrWhiteSpace(_password));

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var encoded = header.Substring("Basic ".Length).Trim();
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var idx = raw.IndexOf(':');
            if (idx < 0)
                return false;

            var user = raw[..idx];
            var pass = raw[(idx + 1)..];
            return user == _username && pass == _password;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePrefixPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "/api/v1/";

        var normalized = value.Trim();
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        if (!normalized.EndsWith('/')) normalized += "/";
        return normalized;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            if (_serverTask != null)
                await _serverTask;
        }
        catch
        {
            // ignore
        }
        finally
        {
            _listener?.Close();
            _cts?.Dispose();
        }
    }

    public sealed class HttpConfig
    {
        public bool Enabled { get; set; } = true;
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 53123;
        public string PrefixPath { get; set; } = "/api/v1/";
        public bool Debug { get; set; } = false;
        public bool AuthEnabled { get; set; } = false;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}

