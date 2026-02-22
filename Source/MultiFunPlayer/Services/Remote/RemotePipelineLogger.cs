using System.IO;

namespace MultiFunPlayer.Remote;

internal static class RemotePipelineLogger
{
    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "MultiFunPlayer-Remote.log");

    public static void Log(string module, string message)
    {
        var line = $"[{DateTime.Now:O}] [{module}] {message}{Environment.NewLine}";
        lock (Sync)
        {
            File.AppendAllText(FilePath, line);
        }
    }
}
