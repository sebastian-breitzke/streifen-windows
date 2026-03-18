using System.Diagnostics;
using System.Threading;

namespace Streifen.Windows;

public static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    public static void Main()
    {
        // Single instance check via named mutex
        const string mutexName = "Global\\Streifen.Windows.SingleInstance";
        _mutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is running — kill it and take over
            KillOtherInstances();
            Thread.Sleep(500);

            _mutex.Dispose();
            _mutex = new Mutex(true, mutexName, out createdNew);

            if (!createdNew)
            {
                // Still can't acquire — bail
                return;
            }
        }

        Services.StreifenLog.Write("=== Streifen Windows starting ===");

        try
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        finally
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
    }

    private static void KillOtherInstances()
    {
        var currentPid = Environment.ProcessId;
        var processName = Process.GetCurrentProcess().ProcessName;

        foreach (var proc in Process.GetProcessesByName(processName))
        {
            if (proc.Id != currentPid)
            {
                try
                {
                    proc.Kill();
                    Services.StreifenLog.Write($"Killed existing instance PID {proc.Id}");
                }
                catch { }
            }
        }
    }
}
