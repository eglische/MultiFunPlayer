using MultiFunPlayer.Input;
using NLog;
using System.Collections.Concurrent;
using System.Reflection;

namespace MultiFunPlayer.Shortcut;

public interface IShortcutActionRunner
{
    bool ScheduleInvoke(IEnumerable<IShortcutActionConfiguration> configurations, IInputGestureData gestureData, Action callback);

    Task<bool> TryInvokeWithFeedbackAsync(string actionName, object[] args);

    void Invoke(string actionName, bool invokeDirectly);
    void Invoke<T0>(string actionName, T0 arg0, bool invokeDirectly);
    void Invoke<T0, T1>(string actionName, T0 arg0, T1 arg1, bool invokeDirectly);
    void Invoke<T0, T1, T2>(string actionName, T0 arg0, T1 arg1, T2 arg2, bool invokeDirectly);
    void Invoke<T0, T1, T2, T3>(string actionName, T0 arg0, T1 arg1, T2 arg2, T3 arg3, bool invokeDirectly);
    void Invoke<T0, T1, T2, T3, T4>(string actionName, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, bool invokeDirectly);

    ValueTask InvokeAsync(string actionName, bool invokeDirectly);
    ValueTask InvokeAsync<T0>(string actionName, T0 arg0, bool invokeDirectly);
    ValueTask InvokeAsync<T0, T1>(string actionName, T0 arg0, T1 arg1, bool invokeDirectly);
    ValueTask InvokeAsync<T0, T1, T2>(string actionName, T0 arg0, T1 arg1, T2 arg2, bool invokeDirectly);
    ValueTask InvokeAsync<T0, T1, T2, T3>(string actionName, T0 arg0, T1 arg1, T2 arg2, T3 arg3, bool invokeDirectly);
    ValueTask InvokeAsync<T0, T1, T2, T3, T4>(string actionName, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, bool invokeDirectly);
}

internal sealed class ShortcutActionRunner : IShortcutActionRunner, IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IShortcutActionResolver _actionResolver;
    private BlockingCollection<(IInvokableItem Item, Action Callback)> _scheduledItems;
    private Thread _thread;

    public ShortcutActionRunner(IShortcutActionResolver actionResolver)
    {
        _actionResolver = actionResolver;
        _scheduledItems = new();
        _thread = new Thread(ConsumeItems);
        _thread.Start();
    }

    public async Task<bool> TryInvokeWithFeedbackAsync(string actionName, object[] args)
    {
        if (!_actionResolver.TryGetAction(actionName, out var action))
        {
            Logger.Warn("Action '{ActionName}' not found in resolver", actionName);
            return false;
        }

        var methods = action.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == "Invoke" && m.GetParameters().Length == args.Length)
            .ToList();

        MethodInfo methodToInvoke = null;

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            bool isMatch = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                var expected = parameters[i].ParameterType;
                var actual = args[i]?.GetType();

                if (actual == null || !expected.IsAssignableFrom(actual))
                {
                    isMatch = false;
                    break;
                }
            }
            if (isMatch)
            {
                methodToInvoke = method;
                break;
            }
        }

        if (methodToInvoke == null)
        {
            Logger.Warn("No matching Invoke method found for action '{ActionName}' with argument types: {Types}",
                actionName, string.Join(", ", args.Select(a => a?.GetType().FullName ?? "null")));
            return false;
        }

        try
        {
            var result = methodToInvoke.Invoke(action, args);
            if (result is ValueTask task)
                await task;
            else
                Logger.Warn("Invoke method did not return a ValueTask for action '{ActionName}'", actionName);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Exception thrown while invoking action '{ActionName}'");
            return false;
        }
    }

    private void ConsumeItems()
    {
        foreach (var (item, callback) in _scheduledItems.GetConsumingEnumerable())
        {
            try
            {
                item.Invoke(_actionResolver);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error during scheduled action invocation");
            }
            finally
            {
                callback?.Invoke();
            }
        }
    }

    public bool ScheduleInvoke(IEnumerable<IShortcutActionConfiguration> configurations, IInputGestureData gestureData, Action callback)
        => _scheduledItems.TryAdd((new GestureInvokableItem(configurations, gestureData), callback));

    public void Invoke(string actionName, bool invokeDirectly)
        => InvokeAsync(actionName, invokeDirectly).GetAwaiter().GetResult();

    public void Invoke<T0>(string actionName, T0 arg0, bool invokeDirectly)
        => InvokeAsync(actionName, arg0, invokeDirectly).GetAwaiter().GetResult();

    public void Invoke<T0, T1>(string actionName, T0 arg0, T1 arg1, bool invokeDirectly)
        => InvokeAsync(actionName, arg0, arg1, invokeDirectly).GetAwaiter().GetResult();

    public void Invoke<T0, T1, T2>(string actionName, T0 arg0, T1 arg1, T2 arg2, bool invokeDirectly)
        => InvokeAsync(actionName, arg0, arg1, arg2, invokeDirectly).GetAwaiter().GetResult();

    public void Invoke<T0, T1, T2, T3>(string actionName, T0 arg0, T1 arg1, T2 arg2, T3 arg3, bool invokeDirectly)
        => InvokeAsync(actionName, arg0, arg1, arg2, arg3, invokeDirectly).GetAwaiter().GetResult();

    public void Invoke<T0, T1, T2, T3, T4>(string actionName, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, bool invokeDirectly)
        => InvokeAsync(actionName, arg0, arg1, arg2, arg3, arg4, invokeDirectly).GetAwaiter().GetResult();

    public ValueTask InvokeAsync(string actionName, bool invokeDirectly)
    {
        if (_actionResolver.TryGetAction(actionName, out var action) && action is ShortcutAction a0)
            return a0.Invoke();
        return ValueTask.CompletedTask;
    }

    public ValueTask InvokeAsync<T0>(string actionName, T0 arg0, bool invokeDirectly)
    {
        if (_actionResolver.TryGetAction(actionName, out var action) && action is ShortcutAction<T0> a1)
            return a1.Invoke(arg0);
        return ValueTask.CompletedTask;
    }

    public ValueTask InvokeAsync<T0, T1>(string actionName, T0 arg0, T1 arg1, bool invokeDirectly)
    {
        if (_actionResolver.TryGetAction(actionName, out var action) && action is ShortcutAction<T0, T1> a2)
            return a2.Invoke(arg0, arg1);
        return ValueTask.CompletedTask;
    }

    public ValueTask InvokeAsync<T0, T1, T2>(string actionName, T0 arg0, T1 arg1, T2 arg2, bool invokeDirectly)
    {
        if (_actionResolver.TryGetAction(actionName, out var action) && action is ShortcutAction<T0, T1, T2> a3)
            return a3.Invoke(arg0, arg1, arg2);
        return ValueTask.CompletedTask;
    }

    public ValueTask InvokeAsync<T0, T1, T2, T3>(string actionName, T0 arg0, T1 arg1, T2 arg2, T3 arg3, bool invokeDirectly)
    {
        if (_actionResolver.TryGetAction(actionName, out var action) && action is ShortcutAction<T0, T1, T2, T3> a4)
            return a4.Invoke(arg0, arg1, arg2, arg3);
        return ValueTask.CompletedTask;
    }

    public ValueTask InvokeAsync<T0, T1, T2, T3, T4>(string actionName, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, bool invokeDirectly)
    {
        if (_actionResolver.TryGetAction(actionName, out var action) && action is ShortcutAction<T0, T1, T2, T3, T4> a5)
            return a5.Invoke(arg0, arg1, arg2, arg3, arg4);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _scheduledItems?.CompleteAdding();
        _thread?.Join();
        _scheduledItems?.Dispose();
    }

    private interface IInvokableItem
    {
        void Invoke(IShortcutActionResolver actionResolver);
    }

    private sealed class GestureInvokableItem : IInvokableItem
    {
        private readonly IEnumerable<IShortcutActionConfiguration> _configs;
        private readonly IInputGestureData _gesture;

        public GestureInvokableItem(IEnumerable<IShortcutActionConfiguration> configs, IInputGestureData gesture)
        {
            _configs = configs;
            _gesture = gesture;
        }

        public void Invoke(IShortcutActionResolver actionResolver)
        {
            foreach (var config in _configs)
            {
                if (!actionResolver.TryGetAction(config.Name, out var action))
                    continue;

                action.Invoke(config, _gesture).GetAwaiter().GetResult();
            }
        }
    }
}
