using MultiFunPlayer.Common;
using MultiFunPlayer.Settings;
using Newtonsoft.Json.Linq;
using Stylet;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MultiFunPlayer.UI.Controls.ViewModels;

internal sealed class RemoteSettingsViewModel : Screen, IHandle<SettingsMessage>
{
    private const string SettingsFileName = $"{nameof(MultiFunPlayer)}.config.json";
    private static readonly string SettingsFilePath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);

    private readonly IEventAggregator _eventAggregator;

    public bool HttpEnabled { get; set; } = false;
    public bool HttpOpenToLan { get; set; } = false;
    public int HttpPort { get; set; } = 53123;
    public string HttpPrefixPath { get; set; } = "/api/v1/";
    public bool HttpDebug { get; set; } = false;
    public bool HttpAuthEnabled { get; set; } = false;
    public string HttpUsername { get; set; } = "";
    public string HttpPassword { get; set; } = "";

    public bool MqttEnabled { get; set; } = false;
    public string MqttBroker { get; set; } = "localhost:1883";
    public bool VoxtaEnabled { get; set; } = false;
    public string VoxtaHost { get; set; } = "127.0.0.1";
    public int VoxtaPort { get; set; } = 5384;
    public bool VoxtaUseSsl { get; set; } = false;
    public string VoxtaPath { get; set; } = "/hub";
    public string VoxtaClientName { get; set; } = "MultiFunPlayer.Networked";
    public string VoxtaTriggerName { get; set; } = "MFP_Invoke";
    public bool VoxtaDebug { get; set; } = false;
    public string MqttUsername { get; set; } = "mqtt_user";
    public string MqttPassword { get; set; } = "mqtt_pass";
    public string MqttTopic { get; set; } = "multifunplayer/actions";
    public string MqttClientId { get; set; } = "MultiFunMQTTClient";
    public int MqttKeepAliveSeconds { get; set; } = 60;
    public bool MqttAutoReconnect { get; set; } = true;
    public int MqttConnectTimeoutSeconds { get; set; } = 8;
    public int MqttReconnectDelaySeconds { get; set; } = 5;
    public bool MqttDebug { get; set; } = false;

    public RemoteSettingsViewModel(IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator;
        _eventAggregator.Subscribe(this);
        DisplayName = "Remote";
    }

    public void Save()
    {
        var settings = SettingsHelper.ReadOrEmpty(SettingsFilePath);

        var remote = new JObject
        {
            ["Http"] = new JObject
            {
                ["Enabled"] = HttpEnabled,
                ["AccessMode"] = HttpOpenToLan,
                ["Port"] = HttpPort,
                ["PrefixPath"] = HttpPrefixPath,
                ["Debug"] = HttpDebug,
                ["AuthEnabled"] = HttpAuthEnabled,
                ["Username"] = HttpUsername,
                ["PasswordEncrypted"] = EncryptSecret(HttpPassword),
            },
            ["Mqtt"] = new JObject
            {
                ["Enabled"] = MqttEnabled,
                ["Broker"] = MqttBroker,
                ["Username"] = MqttUsername,
                ["PasswordEncrypted"] = EncryptSecret(MqttPassword),
                ["Topic"] = MqttTopic,
                ["ClientId"] = MqttClientId,
                ["KeepAliveSeconds"] = MqttKeepAliveSeconds,
                ["AutoReconnect"] = MqttAutoReconnect,
                ["ConnectTimeoutSeconds"] = MqttConnectTimeoutSeconds,
                ["ReconnectDelaySeconds"] = MqttReconnectDelaySeconds,
                ["Debug"] = MqttDebug,
            }
        };

        settings["Remote"] = remote;

        // Hard overwrite and verify persisted content.
        File.WriteAllText(SettingsFilePath, settings.ToString());

        var verify = SettingsHelper.ReadOrEmpty(SettingsFilePath);
        if (!JToken.DeepEquals(verify["Remote"], remote))
        {
            // One retry with full overwrite if anything interfered.
            File.WriteAllText(SettingsFilePath, settings.ToString());
        }

        _eventAggregator.Publish(new RemoteSettingsAppliedMessage());
    }

    public void Handle(SettingsMessage message)
    {
        var settings = message.Settings;

        if (message.Action == SettingsAction.Loading)
        {
            var remote = settings["Remote"] as JObject;
            var http = remote?["Http"] as JObject;
            var mqtt = remote?["Mqtt"] as JObject;
            var voxta = remote?["Voxta"] as JObject;

            HttpEnabled = http?.Value<bool?>("Enabled") ?? HttpEnabled;
            var accessToken = http?["AccessMode"];
            if (accessToken?.Type == JTokenType.Boolean)
                HttpOpenToLan = accessToken.Value<bool>();
            else
            {
                var accessMode = http?.Value<string>("AccessMode");
                HttpOpenToLan = string.Equals(accessMode, "Lan", StringComparison.OrdinalIgnoreCase);
            }
            HttpPort = http?.Value<int?>("Port") ?? HttpPort;
            HttpPrefixPath = http?.Value<string>("PrefixPath") ?? HttpPrefixPath;
            HttpDebug = http?.Value<bool?>("Debug") ?? HttpDebug;
            HttpAuthEnabled = http?.Value<bool?>("AuthEnabled") ?? HttpAuthEnabled;
            HttpUsername = http?.Value<string>("Username") ?? HttpUsername;
            HttpPassword = DecryptSecret(http?.Value<string>("PasswordEncrypted"))
                           ?? http?.Value<string>("Password")
                           ?? HttpPassword;

            // Backward-compatible host mapping to access mode
            var oldHost = http?.Value<string>("Host");
            if (!string.IsNullOrWhiteSpace(oldHost) && http?["AccessMode"] == null)
                HttpOpenToLan = oldHost != "127.0.0.1";

            MqttEnabled = mqtt?.Value<bool?>("Enabled") ?? MqttEnabled;
            MqttBroker = mqtt?.Value<string>("Broker") ?? MqttBroker;
            MqttUsername = mqtt?.Value<string>("Username") ?? MqttUsername;
            MqttPassword = DecryptSecret(mqtt?.Value<string>("PasswordEncrypted"))
                           ?? mqtt?.Value<string>("Password")
                           ?? MqttPassword;
            MqttTopic = mqtt?.Value<string>("Topic") ?? MqttTopic;
            MqttClientId = mqtt?.Value<string>("ClientId") ?? MqttClientId;
            MqttKeepAliveSeconds = mqtt?.Value<int?>("KeepAliveSeconds") ?? MqttKeepAliveSeconds;
            MqttAutoReconnect = mqtt?.Value<bool?>("AutoReconnect") ?? MqttAutoReconnect;
            MqttConnectTimeoutSeconds = mqtt?.Value<int?>("ConnectTimeoutSeconds") ?? MqttConnectTimeoutSeconds;
            MqttReconnectDelaySeconds = mqtt?.Value<int?>("ReconnectDelaySeconds") ?? MqttReconnectDelaySeconds;
            MqttDebug = mqtt?.Value<bool?>("Debug") ?? MqttDebug;

            VoxtaEnabled = voxta?.Value<bool?>("Enabled") ?? VoxtaEnabled;
            VoxtaHost = voxta?.Value<string>("Host") ?? VoxtaHost;
            VoxtaPort = voxta?.Value<int?>("Port") ?? VoxtaPort;
            VoxtaUseSsl = voxta?.Value<bool?>("UseSsl") ?? VoxtaUseSsl;
            VoxtaPath = voxta?.Value<string>("Path") ?? VoxtaPath;
            VoxtaClientName = voxta?.Value<string>("ClientName") ?? VoxtaClientName;
            VoxtaTriggerName = voxta?.Value<string>("TriggerName") ?? VoxtaTriggerName;
            VoxtaDebug = voxta?.Value<bool?>("Debug") ?? VoxtaDebug;
        }
    }

    private static string EncryptSecret(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string DecryptSecret(string encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
            return null;

        try
        {
            var protectedBytes = Convert.FromBase64String(encrypted);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}

