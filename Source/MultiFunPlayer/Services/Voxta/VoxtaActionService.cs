using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using MultiFunPlayer.Common;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.Remote;
using NLog;

namespace MultiFunPlayer.Voxta;

public sealed class VoxtaActionService : IAsyncDisposable
{
    private readonly IShortcutActionRunner _actionRunner;
    private readonly IShortcutManager _shortcutManager;
    private readonly VoxtaConfig _config;
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private HubConnection _connection;
    private readonly HttpClient _httpClient = new();
    private string _sessionId;
    private readonly bool _debug;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public VoxtaActionService(IShortcutActionRunner actionRunner, IShortcutManager shortcutManager, VoxtaConfig config)
    {
        _actionRunner = actionRunner;
        _shortcutManager = shortcutManager;
        _config = config;
        _debug = config.Debug;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return;

        if (IsConnected)
            return;

        var scheme = _config.UseSsl ? "https" : "http";
        var hubUrl = $"{scheme}://{_config.Host}:{_config.Port}{_config.Path}";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _connection.On<JsonElement>("ReceiveMessage", HandleMessage);
        _connection.Reconnected += async _ =>
        {
            await AuthenticateAsync();
            await EnsureSubscribedToActiveChatAsync();
        };

        if (_debug) RemotePipelineLogger.Log("VOXTA-API", $"Connect attempt {hubUrl}");
        await _connection.StartAsync(cancellationToken);
        await AuthenticateAsync();
        await EnsureSubscribedToActiveChatAsync();

        _logger.Info("VoxtaActionService connected to {HubUrl}", hubUrl);
        if (_debug) RemotePipelineLogger.Log("VOXTA-API", $"Connected {hubUrl}");
    }

    private async Task AuthenticateAsync()
    {
        if (_connection == null || _connection.State != HubConnectionState.Connected)
            return;

        var authJson = $$"""
        {
          "$type": "authenticate",
          "client": "{{_config.ClientName}}",
          "clientVersion": "1.0.0",
          "scope": ["role:app", "role:inspector"],
          "capabilities": {}
        }
        """;
        using var doc = JsonDocument.Parse(authJson);
        await _connection.InvokeAsync("SendMessage", new[] { doc.RootElement.Clone() });
        if (_debug) RemotePipelineLogger.Log("VOXTA-API", "Authenticate sent");
    }

    private void HandleMessage(JsonElement message)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!message.TryGetProperty("$type", out var typeEl)) return;
                var type = typeEl.GetString();
                if (_debug) RemotePipelineLogger.Log("VOXTA-API", $"Incoming type={type}");

                if (string.Equals(type, "welcome", StringComparison.OrdinalIgnoreCase))
                {
                    await EnsureSubscribedToActiveChatAsync();
                    return;
                }

                if (string.Equals(type, "chatsSessionsUpdated", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleSessionsUpdated(message);
                    return;
                }

                if (string.Equals(type, "chatStarted", StringComparison.OrdinalIgnoreCase))
                {
                    if (message.TryGetProperty("sessionId", out var sid))
                        await SubscribeToChatAsync(sid.GetString());
                    return;
                }

                if (string.Equals(type, "chatClosed", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "chatEnded", StringComparison.OrdinalIgnoreCase))
                {
                    _sessionId = null;
                    return;
                }

                if (string.Equals(type, "appTrigger", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleAppTrigger(message);
                    return;
                }

                if (string.Equals(type, "action", StringComparison.OrdinalIgnoreCase))
                    await HandleActionMessage(message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Voxta message handling failed");
            }
        });
    }

    private async Task EnsureSubscribedToActiveChatAsync()
    {
        try
        {
            var scheme = _config.UseSsl ? "https" : "http";
            var url = $"{scheme}://{_config.Host}:{_config.Port}/api/chats/sessions";
            using var res = await _httpClient.GetAsync(url);
            if (!res.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return;

            var first = doc.RootElement[0];
            if (first.TryGetProperty("sessionId", out var sid))
                await SubscribeToChatAsync(sid.GetString());
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Failed to query active Voxta sessions");
        }
    }

    private async Task HandleSessionsUpdated(JsonElement message)
    {
        if (!message.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array)
            return;

        if (sessions.GetArrayLength() == 0)
        {
            _sessionId = null;
            return;
        }

        var first = sessions[0];
        if (first.TryGetProperty("sessionId", out var sid))
            await SubscribeToChatAsync(sid.GetString());
    }

    private async Task SubscribeToChatAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || _sessionId == sessionId)
            return;

        if (_connection == null || _connection.State != HubConnectionState.Connected)
            return;

        var json = $$"""
        {
          "$type": "subscribeToChat",
          "sessionId": "{{sessionId}}"
        }
        """;

        using var doc = JsonDocument.Parse(json);
        await _connection.InvokeAsync("SendMessage", new[] { doc.RootElement.Clone() });
        _sessionId = sessionId;
        _logger.Info("VoxtaActionService subscribed to session {SessionId}", sessionId);
        if (_debug) RemotePipelineLogger.Log("VOXTA-API", $"Subscribed session {sessionId}");
    }

    private async Task HandleAppTrigger(JsonElement message)
    {
        var name = message.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (!message.TryGetProperty("arguments", out var argsEl) || argsEl.ValueKind != JsonValueKind.Array)
            argsEl = default;

        // Mode 1: compatibility mode -> fixed trigger name with JSON payload in arg0
        if (string.Equals(name, _config.TriggerName, StringComparison.OrdinalIgnoreCase)
            && argsEl.ValueKind == JsonValueKind.Array
            && argsEl.GetArrayLength() > 0
            && argsEl[0].ValueKind == JsonValueKind.String)
        {
            var payload = argsEl[0].GetString();
            if (!string.IsNullOrWhiteSpace(payload))
            {
                using var doc = JsonDocument.Parse(payload);
                await InvokeFromPayload(doc.RootElement);
                return;
            }
        }

        // Mode 2: native Voxta action style -> trigger name IS action name, args are positional values
        var values = argsEl.ValueKind == JsonValueKind.Array
            ? argsEl.EnumerateArray().Select(x => x.Clone()).ToArray()
            : Array.Empty<JsonElement>();

        await InvokeFromActionAndValues(name, values);
    }

    private async Task HandleActionMessage(JsonElement message)
    {
        var actionName = message.TryGetProperty("value", out var v) ? v.GetString() : null;
        if (string.IsNullOrWhiteSpace(actionName))
            return;

        var values = new List<JsonElement>();
        if (message.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in argsEl.EnumerateArray())
            {
                if (arg.TryGetProperty("value", out var value))
                    values.Add(value.Clone());
            }
        }

        await InvokeFromActionAndValues(actionName, values.ToArray());
    }

    private async Task InvokeFromActionAndValues(string actionName, JsonElement[] values)
    {
        var wrappedArgs = values.Select(v => new WrappedValue { Value = v }).ToArray();
        var root = new RootInvokePayload
        {
            Action = actionName,
            Arguments = wrappedArgs
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(root));
        await InvokeFromPayload(doc.RootElement);
    }

    private async Task InvokeFromPayload(JsonElement root)
    {
        if (!root.TryGetProperty("Action", out var actionElement)) return;

        var actionName = actionElement.GetString();
        var resolvedName = ResolveBestMatchingAction(actionName);
        if (resolvedName == null) return;

        var action = _shortcutManager.GetAction(resolvedName);
        var invokeMethods = action.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name.StartsWith("Invoke") && m.ReturnType == typeof(ValueTask))
            .ToList();

        var argElements = root.TryGetProperty("Arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
            ? argsElement.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();

        MethodInfo invokeMethod = null;
        foreach (var method in invokeMethods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != argElements.Length) continue;

            var allMatch = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                try { ConvertJsonToType(argElements[i].GetProperty("Value"), parameters[i].ParameterType); }
                catch { allMatch = false; break; }
            }

            if (allMatch) { invokeMethod = method; break; }
        }

        if (invokeMethod == null) return;

        var expectedTypes = invokeMethod.GetParameters().Select(p => p.ParameterType).ToArray();
        var args = new object[expectedTypes.Length];
        for (var i = 0; i < expectedTypes.Length; i++)
            args[i] = ConvertJsonToType(argElements[i].GetProperty("Value"), expectedTypes[i]);

        var ok = await _actionRunner.TryInvokeWithFeedbackAsync(resolvedName, args);
        if (_debug) RemotePipelineLogger.Log("VOXTA-API", $"Invoke {resolvedName} ({args.Length} args) => {ok}");
    }

    private string ResolveBestMatchingAction(string requestedName)
    {
        var available = _shortcutManager.AvailableActions;
        if (available.Contains(requestedName)) return requestedName;
        var matches = available.Where(a => a.EndsWith(requestedName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static object ConvertJsonToType(JsonElement element, Type targetType)
    {
        if (targetType == typeof(DeviceAxis)) return ParseDeviceAxis(element);

        if (targetType == typeof(string))
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();

        if (targetType == typeof(int))
            return element.ValueKind == JsonValueKind.String ? int.Parse(element.GetString()) : element.GetInt32();

        if (targetType == typeof(double))
            return element.ValueKind == JsonValueKind.String ? double.Parse(element.GetString(), System.Globalization.CultureInfo.InvariantCulture) : element.GetDouble();

        if (targetType == typeof(float))
            return element.ValueKind == JsonValueKind.String ? float.Parse(element.GetString(), System.Globalization.CultureInfo.InvariantCulture) : (float)element.GetDouble();

        if (targetType == typeof(decimal))
            return element.ValueKind == JsonValueKind.String ? decimal.Parse(element.GetString(), System.Globalization.CultureInfo.InvariantCulture) : element.GetDecimal();

        if (targetType == typeof(bool))
            return element.ValueKind == JsonValueKind.String ? bool.Parse(element.GetString()) : element.GetBoolean();

        if (targetType.IsEnum)
            return Enum.Parse(targetType, element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(), true);

        if (targetType == typeof(Guid))
            return element.ValueKind == JsonValueKind.String ? Guid.Parse(element.GetString()) : element.GetGuid();

        return JsonSerializer.Deserialize(element.GetRawText(), targetType);
    }

    private static DeviceAxis ParseDeviceAxis(JsonElement valueElement)
    {
        var raw = valueElement.ValueKind == JsonValueKind.String ? valueElement.GetString() : valueElement.ToString();
        var axis = DeviceAxis.Parse(raw);
        return axis ?? throw new ArgumentException("Invalid DeviceAxis");
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            try { await _connection.StopAsync(); } catch { }
            try { await _connection.DisposeAsync(); } catch { }
        }

        _httpClient.Dispose();
    }

    public sealed class VoxtaConfig
    {
        public bool Enabled { get; set; } = false;
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 5384;
        public bool UseSsl { get; set; } = false;
        public string Path { get; set; } = "/hub";
        public string ClientName { get; set; } = "MultiFunPlayer.Networked";
        public string TriggerName { get; set; } = "MFP_Invoke";
        public bool Debug { get; set; } = false;
    }

    private sealed class RootInvokePayload
    {
        public string Action { get; set; }
        public WrappedValue[] Arguments { get; set; }
    }

    private sealed class WrappedValue
    {
        public JsonElement Value { get; set; }
    }
}
