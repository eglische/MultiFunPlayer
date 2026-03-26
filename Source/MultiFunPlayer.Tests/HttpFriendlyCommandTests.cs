using System.Text.Json;
using MultiFunPlayer.Http;

namespace MultiFunPlayer.Tests;

public sealed class HttpFriendlyCommandTests
{
    [Theory]
    [InlineData("stroker_robot:stroke_speed:medium", "L0", "medium")]
    [InlineData("stroker_robot:twist_speed:fast", "R0", "fast")]
    public void TryCreate_ParsesLegacySpeedCommands(string command, string axis, string label)
    {
        var success = HttpFriendlyCommand.TryCreate(command, out var plan, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Contains(axis, plan.Summary);
        Assert.Contains(label, plan.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sine", plan.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plan.Invocations, x => x.ActionName == "Axis::MotionProviderBlend::Set");
        Assert.Contains(plan.Invocations, x => x.ActionName == "MotionProvider::Pattern::Speed::Set");
    }

    [Fact]
    public void TryCreate_ParsesModeCommand()
    {
        var success = HttpFriendlyCommand.TryCreate("stroker_robot:mode:funscript", out var plan, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal("Switched L0 and R0 to funscript", plan.Summary);
        Assert.Equal(2, plan.Invocations.Count);
        Assert.All(plan.Invocations, x => Assert.Equal("Axis::MotionProviderBlend::Set", x.ActionName));
    }

    [Fact]
    public void TryCreate_ParsesJsonNumericSpeed()
    {
        using var doc = JsonDocument.Parse("""{"Axis":"L0","Speed":500,"Pattern":"Triangle","Min":0.2,"Max":0.8}""");

        var success = HttpFriendlyCommand.TryCreate(doc.RootElement, out var plan, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Contains("L0", plan.Summary);
        Assert.Contains("Triangle", plan.Summary, StringComparison.OrdinalIgnoreCase);
        var speedInvocation = Assert.Single(plan.Invocations.Where(x => x.ActionName == "MotionProvider::Pattern::Speed::Set"));
        Assert.Equal(5.0, Assert.IsType<double>(speedInvocation.Arguments[1]));
        var minInvocation = Assert.Single(plan.Invocations.Where(x => x.ActionName == "MotionProvider::Pattern::Minimum::Set"));
        var maxInvocation = Assert.Single(plan.Invocations.Where(x => x.ActionName == "MotionProvider::Pattern::Maximum::Set"));
        Assert.Equal(0.2, Assert.IsType<double>(minInvocation.Arguments[1]));
        Assert.Equal(0.8, Assert.IsType<double>(maxInvocation.Arguments[1]));
    }

    [Fact]
    public void TryCreate_StopUsesZeroSpeedAndSmallRange()
    {
        using var doc = JsonDocument.Parse("""{"Target":"stroke","Speed":"stop"}""");

        var success = HttpFriendlyCommand.TryCreate(doc.RootElement, out var plan, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(plan);
        var speedInvocation = Assert.Single(plan.Invocations.Where(x => x.ActionName == "MotionProvider::Pattern::Speed::Set"));
        var minInvocation = Assert.Single(plan.Invocations.Where(x => x.ActionName == "MotionProvider::Pattern::Minimum::Set"));
        var maxInvocation = Assert.Single(plan.Invocations.Where(x => x.ActionName == "MotionProvider::Pattern::Maximum::Set"));
        Assert.Equal(0.0, Assert.IsType<double>(speedInvocation.Arguments[1]));
        Assert.Equal(0.0, Assert.IsType<double>(minInvocation.Arguments[1]));
        Assert.Equal(0.01, Assert.IsType<double>(maxInvocation.Arguments[1]));
    }

    [Fact]
    public void TryCreate_RejectsUnknownCommand()
    {
        var success = HttpFriendlyCommand.TryCreate("stroker_robot:unknown:fast", out var plan, out var error);

        Assert.False(success);
        Assert.Null(plan);
        Assert.NotNull(error);
    }
}
