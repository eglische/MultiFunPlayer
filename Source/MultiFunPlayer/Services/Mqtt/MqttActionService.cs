using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using NLog;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.Common;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MultiFunPlayer.Mqtt;

public sealed class MqttActionService : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly IShortcutActionRunner _actionRunner;
    private readonly IShortcutManager _shortcutManager;
    private readonly string _topic;
    private readonly string _errorTopic;
    private readonly string _logFilePath = "mqtt-debug-log.txt";
    private readonly bool _debug;
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public MqttActionService(IShortcutActionRunner actionRunner, IShortcutManager shortcutManager)
    {
        _actionRunner = actionRunner;
        _shortcutManager = shortcutManager;

        var config = LoadConfig();
        _debug = config.Debug;
        _topic = config.Topic;
        _errorTopic = config.Topic + "/errors";

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _options = new MqttClientOptionsBuilder()
            .WithClientId(config.ClientId)
            .WithTcpServer(config.Broker.Split(':')[0], int.Parse(config.Broker.Split(':')[1]))
            .WithCredentials(config.Username, config.Password)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(config.KeepAliveSeconds))
            .Build();

        _client.ConnectedAsync += OnConnected;
        _client.DisconnectedAsync += OnDisconnected;
        _client.ApplicationMessageReceivedAsync += OnMessageReceived;

        if (_debug)
        {
            LogDebug("MqttActionService initialized at startup.");
            LogAvailableActions();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(_options, cancellationToken);

        if (_debug)
            LogDebug("Connected and subscribed to MQTT broker.");
    }

    private async Task OnConnected(MqttClientConnectedEventArgs args)
    {
        _logger.Info("Connected to MQTT broker");
        var topicFilter = new MqttTopicFilterBuilder()
            .WithTopic(_topic)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.SubscribeAsync(topicFilter);
    }

    private async Task OnDisconnected(MqttClientDisconnectedEventArgs args)
    {
        _logger.Warn("Disconnected from MQTT broker");
        await Task.Delay(5000);
        try { await _client.ConnectAsync(_options); }
        catch (Exception ex) { _logger.Error(ex, "Reconnect failed"); }
    }

    private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs args)
    {
        string payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload);

        if (_debug)
            LogDebug($"Received: {payload}");

        try
        {
            LogDebug($"Runner Available Actions: {string.Join(", ", _shortcutManager.AvailableActions)}");

            var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (!root.TryGetProperty("Action", out var actionElement))
                throw new FormatException("Missing Action field");

            var actionName = actionElement.GetString();
            var resolvedName = ResolveBestMatchingAction(actionName);
            if (resolvedName == null)
                throw new ArgumentException($"Unknown action: {actionName}");

            var action = _shortcutManager.GetAction(resolvedName);
            var invokeMethods = action.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name.StartsWith("Invoke") && m.ReturnType == typeof(ValueTask))
                .ToList();

            MethodInfo invokeMethod = null;
            JsonElement[] argElements = root.TryGetProperty("Arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
                ? argsElement.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();

            foreach (var method in invokeMethods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != argElements.Length)
                    continue;

                bool allMatch = true;
                for (int i = 0; i < parameters.Length; i++)
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
                throw new InvalidOperationException($"No matching Invoke method found for action {resolvedName}");

            var expectedTypes = invokeMethod.GetParameters().Select(p => p.ParameterType).ToArray();
            LogDebug($"Resolved action expects Arg: Count: {expectedTypes.Length}");
            for (int i = 0; i < expectedTypes.Length; i++)
            {
                var type = expectedTypes[i];
                if (type == typeof(DeviceAxis))
                {
                    var validAxes = string.Join(", ", DeviceAxis.All.Select(a => a.Name));
                    LogDebug($"  - Arg{i + 1}: {type.Name} (valid: {validAxes})");
                }
                else
                {
                    LogDebug($"  - Arg{i + 1}: {type.Name}");
                }
            }

            var argsList = new object[expectedTypes.Length];
            for (int i = 0; i < expectedTypes.Length; i++)
            {
                var value = ConvertJsonToType(argElements[i].GetProperty("Value"), expectedTypes[i]);
                argsList[i] = value;
                LogDebug($"  Parsed Arg{i + 1}: {value?.GetType().FullName ?? "null"} = {value}");
            }

            await TryInvokeAction(resolvedName, argsList);
        }
        catch (Exception ex)
        {
            await SendErrorAsync($"Error: {ex.Message}");
            LogError(ex.ToString());
        }
    }

    private object ConvertJsonToType(JsonElement element, Type targetType)
    {
        try
        {
            if (targetType == typeof(DeviceAxis))
                return ParseDeviceAxis(element);
            else if (targetType.IsEnum)
                return Enum.Parse(targetType, element.GetString(), ignoreCase: true);
            else if (targetType == typeof(Guid))
                return element.GetGuid();
            else if (targetType == typeof(string))
                return element.GetString();
            else if (targetType == typeof(int))
                return element.GetInt32();
            else if (targetType == typeof(double))
                return element.GetDouble();
            else if (targetType == typeof(float))
                return (float)element.GetDouble();
            else if (targetType == typeof(decimal))
                return element.GetDecimal();
            else if (targetType == typeof(bool))
                return element.GetBoolean();
            else
                return JsonSerializer.Deserialize(element.GetRawText(), targetType);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to convert value '{element}' to type {targetType.Name}: {ex.Message}", ex);
        }
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

    private async Task TryInvokeAction(string resolvedName, object[] args)
    {
        LogDebug($"Resolved action: {resolvedName}");
        LogDebug("Arguments:");
        for (int i = 0; i < args.Length; i++)
            LogDebug($"  - {args[i]?.GetType().FullName ?? "null"}: {args[i]}");

        try
        {
            bool success = await _actionRunner.TryInvokeWithFeedbackAsync(resolvedName, args);
            if (success)
            {
                LogDebug($"Action executed: {resolvedName}({string.Join(", ", args)})");
            }
            else
            {
                LogError($"Failed to invoke: {resolvedName} with args: {string.Join(", ", args.Select(a => a?.ToString() ?? "null"))}");
                await SendErrorAsync($"Invalid action or arguments: {resolvedName}({string.Join(", ", args)})");
            }
        }
        catch (Exception ex)
        {
            LogError($"Execution error for action {resolvedName}: {ex}");
            await SendErrorAsync($"Execution error for action: {resolvedName}. {ex.Message}");
        }
    }

    private async Task SendErrorAsync(string message)
    {
        var errorPayload = new MqttApplicationMessageBuilder()
            .WithTopic(_errorTopic)
            .WithPayload(message)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(errorPayload);
    }

    private static MqttConfig LoadConfig()
    {
        var configText = File.ReadAllText("MQTT.config.json");
        return JsonSerializer.Deserialize<MqttConfig>(configText)
               ?? throw new Exception("Invalid MQTT config JSON");
    }

    private void LogDebug(string msg)
    {
        _logger.Debug(msg);
        File.AppendAllText(_logFilePath, $"[DEBUG] {DateTime.Now:O} {msg}\n");
    }

    private void LogError(string msg)
    {
        _logger.Error(msg);
        File.AppendAllText(_logFilePath, $"[ERROR] {DateTime.Now:O} {msg}\n");
    }

    private void LogAvailableActions()
    {
        foreach (var name in _shortcutManager.AvailableActions)
            LogDebug($"AvailableAction: {name}");
    }

    public async ValueTask DisposeAsync()
    {
        try { await _client.DisconnectAsync(); } catch { /* ignore */ }
        _client.Dispose();
    }

    private class MqttConfig
    {
        public string Broker { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string Topic { get; set; }
        public string ClientId { get; set; }
        public int KeepAliveSeconds { get; set; }
        public bool AutoReconnect { get; set; }
        public bool Debug { get; set; }
    }
}
