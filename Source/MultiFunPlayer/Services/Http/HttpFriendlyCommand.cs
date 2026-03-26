using System.Text.Json;
using MultiFunPlayer.Common;

namespace MultiFunPlayer.Http;

internal static class HttpFriendlyCommand
{
    private const string CommandPrefix = "stroker_robot";
    private const double TwistSpeedFactor = 0.5;
    private const string DefaultPattern = "Sine";

    public static bool TryCreate(JsonElement root, out FriendlyCommandPlan plan, out string error)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            plan = null;
            error = "Friendly command payload must be a JSON object";
            return false;
        }

        if (TryGetString(root, "Command", out var commandText))
            return TryCreate(commandText, out plan, out error);

        if (TryGetString(root, "Mode", out var mode))
            return TryCreateModePlan(mode, ReadOptionalString(root, "Pattern"), out plan, out error);

        var axis = ResolveAxis(root);
        if (axis == null)
        {
            plan = null;
            error = "Friendly command requires Axis or Target when setting speed";
            return false;
        }

        if (TryResolveSpeedToken(root, out var speedToken))
            return TryCreateSpeedPlan(axis, speedToken, ReadOptionalString(root, "Pattern"), ReadOptionalDouble(root, "Min"), ReadOptionalDouble(root, "Max"), out plan, out error);

        plan = null;
        error = "Friendly command payload must include Command, Mode, or Speed";
        return false;
    }

    public static bool TryCreate(string commandText, out FriendlyCommandPlan plan, out string error)
    {
        var normalized = commandText?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            plan = null;
            error = "Friendly command text cannot be empty";
            return false;
        }

        var parts = normalized.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], CommandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            plan = null;
            error = $"Friendly command must look like '{CommandPrefix}:stroke_speed:medium'";
            return false;
        }

        return parts[1].ToLowerInvariant() switch
        {
            "stroke_speed" => TryCreateSpeedPlan(DeviceAxis.Parse("L0"), parts[2], DefaultPattern, null, null, out plan, out error),
            "twist_speed" => TryCreateSpeedPlan(DeviceAxis.Parse("R0"), parts[2], DefaultPattern, null, null, out plan, out error),
            "mode" => TryCreateModePlan(parts[2], DefaultPattern, out plan, out error),
            _ => Fail(out plan, out error, $"Unknown friendly command '{parts[1]}'")
        };
    }

    public static object Describe() => new
    {
        endpoint = "/api/v1/command",
        accepts = new object[]
        {
            new
            {
                Command = "stroker_robot:stroke_speed:medium"
            },
            new
            {
                Command = "stroker_robot:twist_speed:500"
            },
            new
            {
                Command = "stroker_robot:mode:funscript"
            },
            new
            {
                Axis = "L0",
                Speed = 500
            },
            new
            {
                Target = "twist",
                Speed = "fast",
                Pattern = "Triangle"
            },
            new
            {
                Mode = "motionprovider",
                Pattern = "Sine"
            },
            new
            {
                Axis = "L0",
                Speed = "stop",
                Min = 0.0,
                Max = 0.01
            }
        },
        notes = new[]
        {
            "stroke maps to L0, twist maps to R0",
            "speed labels: stop, very_slow, slow, medium, fast, very_fast",
            "numeric speeds 0-1500 map to provider speed 0.00-15.00",
            "mode funscript sets blend to 0 on L0 and R0",
            "mode motionprovider sets blend to 1 and provider Pattern on L0 and R0",
            "default pattern is Sine unless Pattern is provided",
            "optional Min and Max crop the motion range",
            "stop maps to min 0, max 0.01, speed 0"
        }
    };

    private static bool TryCreateModePlan(string mode, string patternName, out FriendlyCommandPlan plan, out string error)
    {
        var normalizedMode = mode?.Trim().ToLowerInvariant();
        switch (normalizedMode)
        {
            case "funscript":
                plan = new FriendlyCommandPlan(
                    "Switched L0 and R0 to funscript",
                    [
                        SetBlend("L0", 0),
                        SetBlend("R0", 0)
                    ]);
                error = null;
                return true;

            case "motionprovider":
                if (!TryResolvePattern(patternName, out var resolvedPattern, out error))
                {
                    plan = null;
                    return false;
                }

                plan = new FriendlyCommandPlan(
                    $"Switched L0 and R0 to pattern motion provider ({resolvedPattern})",
                    CreateMotionProviderSetupInvocations(resolvedPattern));
                return true;

            default:
                plan = null;
                error = "Mode must be 'funscript' or 'motionprovider'";
                return false;
        }
    }

    private static bool TryCreateSpeedPlan(DeviceAxis axis, string speedToken, string patternName, double? minimumOverride, double? maximumOverride,
        out FriendlyCommandPlan plan, out string error)
    {
        if (!TryResolveSpeedProfile(speedToken, out var profile, out error))
        {
            plan = null;
            return false;
        }

        if (!TryResolvePattern(patternName, out var resolvedPattern, out error))
        {
            plan = null;
            return false;
        }

        var minimum = minimumOverride.HasValue ? MathUtils.Clamp01(minimumOverride.Value) : profile.Minimum;
        var maximum = maximumOverride.HasValue ? MathUtils.Clamp01(maximumOverride.Value) : profile.Maximum;
        if (maximum < minimum)
        {
            plan = null;
            error = "Max must be greater than or equal to Min";
            return false;
        }

        var effectiveSpeed = axis == DeviceAxis.Parse("R0") && profile.ProviderSpeed > 0
            ? profile.ProviderSpeed * TwistSpeedFactor
            : profile.ProviderSpeed;

        var axisName = axis.Name;
        var invocations = CreateMotionProviderSetupInvocations(resolvedPattern);
        invocations.Add(SetPatternMinimum(axisName, minimum));
        invocations.Add(SetPatternMaximum(axisName, maximum));
        invocations.Add(SetPatternSpeed(axisName, effectiveSpeed));

        plan = new FriendlyCommandPlan(
            $"{axisName} => {profile.Label} ({resolvedPattern})",
            invocations);
        error = null;
        return true;
    }

    private static bool TryResolveSpeedProfile(string speedToken, out SpeedProfile profile, out string error)
    {
        var normalized = speedToken?.Trim().ToLowerInvariant();
        profile = normalized switch
        {
            "stop" => new SpeedProfile("stop", 0.0, 0.0, 0.01),
            "start" => new SpeedProfile("medium", 4.5, 0.0, 1.0),
            "very_slow" => new SpeedProfile("very_slow", 1.0, 0.0, 1.0),
            "slow" => new SpeedProfile("slow", 2.5, 0.0, 1.0),
            "medium" => new SpeedProfile("medium", 4.5, 0.0, 1.0),
            "fast" => new SpeedProfile("fast", 6.5, 0.0, 1.0),
            "very_fast" => new SpeedProfile("very_fast", 9.0, 0.0, 1.0),
            _ => null
        };

        if (profile != null)
        {
            error = null;
            return true;
        }

        if (double.TryParse(normalized, out var numericSpeed))
        {
            numericSpeed = Math.Clamp(numericSpeed, 0, 1500);
            profile = numericSpeed <= 0
                ? new SpeedProfile("stop", 0.0, 0.0, 0.01)
                : new SpeedProfile($"{numericSpeed:0}", numericSpeed / 100.0, 0.0, 1.0);
            error = null;
            return true;
        }

        error = "Speed must be one of stop/start/very_slow/slow/medium/fast/very_fast or a number between 0 and 1500";
        return false;
    }

    private static bool TryResolvePattern(string patternName, out string resolvedPattern, out string error)
    {
        var candidate = string.IsNullOrWhiteSpace(patternName) ? DefaultPattern : patternName.Trim();
        if (!Enum.TryParse<MotionProvider.ViewModels.PatternType>(candidate, ignoreCase: true, out var pattern))
        {
            resolvedPattern = null;
            error = $"Unknown Pattern '{patternName}'. Use Triangle, Sine, DoubleBounce, SharpBounce, Saw, or Square";
            return false;
        }

        resolvedPattern = pattern.ToString();
        error = null;
        return true;
    }

    private static bool TryResolveSpeedToken(JsonElement root, out string speedToken)
    {
        if (TryGetString(root, "Speed", out speedToken))
            return true;

        if (root.TryGetProperty("Speed", out var speedElement) && speedElement.ValueKind is JsonValueKind.Number)
        {
            speedToken = speedElement.GetRawText();
            return true;
        }

        if (TryGetString(root, "State", out var state))
        {
            speedToken = state;
            return true;
        }

        speedToken = null;
        return false;
    }

    private static DeviceAxis ResolveAxis(JsonElement root)
    {
        if (TryGetString(root, "Axis", out var axisName))
            return DeviceAxis.Parse(axisName);

        if (!TryGetString(root, "Target", out var target))
            return null;

        return target.Trim().ToLowerInvariant() switch
        {
            "stroke" => DeviceAxis.Parse("L0"),
            "twist" => DeviceAxis.Parse("R0"),
            _ => DeviceAxis.Parse(target)
        };
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        if (root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        value = null;
        return false;
    }

    private static string ReadOptionalString(JsonElement root, string propertyName)
        => TryGetString(root, propertyName, out var value) ? value : null;

    private static double? ReadOptionalDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetDouble(),
            JsonValueKind.String when double.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static List<ShortcutInvocation> CreateMotionProviderSetupInvocations(string patternName)
    {
        return
        [
            SetBlend("L0", 1),
            SetBlend("R0", 1),
            SetPattern("L0", patternName),
            SetPattern("R0", patternName),
            SetMotionProvider("L0", "Pattern"),
            SetMotionProvider("R0", "Pattern")
        ];
    }

    private static ShortcutInvocation SetBlend(string axisName, double value)
        => new("Axis::MotionProviderBlend::Set", [DeviceAxis.Parse(axisName), value]);

    private static ShortcutInvocation SetPattern(string axisName, string value)
        => new("MotionProvider::Pattern::Pattern::Set", [DeviceAxis.Parse(axisName), Enum.Parse<MotionProvider.ViewModels.PatternType>(value, ignoreCase: true)]);

    private static ShortcutInvocation SetMotionProvider(string axisName, string value)
        => new("Axis::MotionProvider::Set", [DeviceAxis.Parse(axisName), value]);

    private static ShortcutInvocation SetPatternSpeed(string axisName, double value)
        => new("MotionProvider::Pattern::Speed::Set", [DeviceAxis.Parse(axisName), value]);

    private static ShortcutInvocation SetPatternMinimum(string axisName, double value)
        => new("MotionProvider::Pattern::Minimum::Set", [DeviceAxis.Parse(axisName), value]);

    private static ShortcutInvocation SetPatternMaximum(string axisName, double value)
        => new("MotionProvider::Pattern::Maximum::Set", [DeviceAxis.Parse(axisName), value]);

    private static bool Fail(out FriendlyCommandPlan plan, out string error, string message)
    {
        plan = null;
        error = message;
        return false;
    }

    internal sealed record FriendlyCommandPlan(string Summary, IReadOnlyList<ShortcutInvocation> Invocations);
    internal sealed record ShortcutInvocation(string ActionName, object[] Arguments);
    private sealed record SpeedProfile(string Label, double ProviderSpeed, double Minimum, double Maximum);
}
