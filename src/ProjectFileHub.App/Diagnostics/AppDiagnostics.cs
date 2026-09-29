namespace ProjectFileHub.App.Diagnostics;

internal static class AppDiagnostics
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectFileHub",
        "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "startup.log");
    private static readonly string StartupPendingPath = Path.Combine(LogDirectory, "startup.pending");
    private static readonly string RuntimePendingPath = Path.Combine(LogDirectory, "runtime.pending");

    public static bool PreviousStartupFailed { get; private set; }

    public static void StartSession()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
            {
                File.Move(LogPath, Path.Combine(LogDirectory, "startup.previous.log"), overwrite: true);
            }

            PreviousStartupFailed = File.Exists(StartupPendingPath);
            var previousRuntimeEndedUnexpectedly = File.Exists(RuntimePendingPath);
            File.WriteAllText(StartupPendingPath, $"{DateTimeOffset.Now:O}|{Environment.ProcessId}");
            File.WriteAllText(RuntimePendingPath, $"{DateTimeOffset.Now:O}|{Environment.ProcessId}");
            Log($"Session start · PID {Environment.ProcessId} · {Environment.Version}");
            if (PreviousStartupFailed)
            {
                Log("Previous startup did not reach the stable marker; safe startup is enabled");
            }
            if (previousRuntimeEndedUnexpectedly)
            {
                Log("Previous session did not record a normal exit");
            }
        }
        catch
        {
            // Diagnostics must never prevent the app from opening.
        }
    }

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    LogPath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never become a second failure source.
        }
    }

    public static void Log(string message, Exception exception) =>
        Log($"{message} | HRESULT=0x{exception.HResult:X8} | {exception}");

    public static void MarkStartupStable()
    {
        try
        {
            if (File.Exists(StartupPendingPath))
            {
                File.Delete(StartupPendingPath);
            }

            Log("Startup marked stable");
        }
        catch
        {
            // A failed marker cleanup must not affect the running app.
        }
    }

    public static void MarkSessionClosed()
    {
        try
        {
            if (File.Exists(RuntimePendingPath))
            {
                File.Delete(RuntimePendingPath);
            }

            Log("Session closed normally");
        }
        catch
        {
            // Diagnostics must never prevent a normal application exit.
        }
    }
}
