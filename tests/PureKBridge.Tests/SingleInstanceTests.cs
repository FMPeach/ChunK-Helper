using System.Diagnostics;
using PureKBridge.App.Services;

namespace PureKBridge.Tests;

internal static class SingleInstanceTests
{
    public static int RunProbe(string[] args)
    {
        using var instance = new SingleInstanceService(new AppLog(), args[1]);
        if (instance.IsPrimary)
        {
            instance.ListenForActivation(() => Task.CompletedTask);
            Console.WriteLine("PRIMARY");
            if (args.Contains("--hold") && Console.ReadLine() == "crash") Environment.Exit(7);
        }
        else
        {
            instance.ActivateExistingAsync().GetAwaiter().GetResult();
            Console.WriteLine("SECONDARY");
        }
        return 0;
    }

    private static Process StartProbe(string name, bool hold = false)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--single-instance-probe");
        start.ArgumentList.Add(name);
        if (hold) start.ArgumentList.Add("--hold");
        return Process.Start(start)!;
    }

    public static async Task<bool> ActivateAsync(string name)
    {
        using var child = StartProbe(name);
        try
        {
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (child.ExitCode != 0) throw new Exception(await error);
            return (await output).Trim() == "SECONDARY";
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var name = "ChunK.Helper.Test." + Guid.NewGuid().ToString("N");
        var log = new AppLog();
        var activations = 0;
        using (var primary = new SingleInstanceService(log, name))
        {
            check(primary.IsPrimary, "首次启动取得单实例所有权");
            // A launch arriving before the window is ready must wait for activation, not start again.
            var earlyLaunch = ActivateAsync(name);
            primary.ListenForActivation(() => { Interlocked.Increment(ref activations); return Task.CompletedTask; });
            check(await earlyLaunch && activations == 1, "首个窗口初始化期间再次启动仍唤醒原实例");
            var launches = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ActivateAsync(name)));
            check(launches.All(result => result) && activations == 5, "同时重复启动全部交给原实例处理");
        }
        using (var restarted = new SingleInstanceService(log, name))
            check(restarted.IsPrimary, "正常退出后可以重新启动");

        using var crashed = StartProbe(name, hold: true);
        try
        {
            check(await crashed.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "PRIMARY",
                "独立测试进程取得单实例所有权");
            using var observer = new SingleInstanceService(log, name);
            check(!observer.IsPrimary, "其他进程不能重复初始化应用");
            await crashed.StandardInput.WriteLineAsync("crash");
            await crashed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var recovered = new SingleInstanceService(log, name);
            check(recovered.IsPrimary, "原实例意外退出后接管遗留互斥量，无需清理文件");
            recovered.ListenForActivation(() => Task.CompletedTask);
            check(await ActivateAsync(name), "异常退出后重启仍可接收窗口唤醒请求");
        }
        finally { if (!crashed.HasExited) { crashed.Kill(); await crashed.WaitForExitAsync(); } }
    }
}
