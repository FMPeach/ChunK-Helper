using System.Reflection;
using System.IO;

namespace PureKBridge.App.Services;

public sealed record LicenseNotice(string Name, string License, string Text);

public static class AboutInfo
{
    private static readonly Assembly Assembly = typeof(AboutInfo).Assembly;
    public static string Version => Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetName().Version?.ToString() ?? "未知版本";

    private static string Resource(string name)
    {
        using var stream = Assembly.GetManifestResourceStream("ChunK.Notices." + name)
            ?? throw new InvalidOperationException("许可证资源缺失：" + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<LicenseNotice> Licenses { get; } =
    [
        new("纯K助手", "MIT", Resource("LICENSE")),
        new("ZXing.Net", "Apache-2.0", "Copyright (c) Michael Jahn and ZXing authors.\n\n" + Resource("Apache-2.0.txt")),
        new(".NET / WPF", "MIT / Third-party notices", Resource("Runtime-LICENSE.txt") + "\n\n" + Resource("Runtime-NOTICES.txt") + "\n\n" + Resource("WPF-LICENSE.txt")),
        new("Microsoft.Windows.SDK.NET.Ref", "Windows SDK License", "Microsoft Corporation\nhttps://aka.ms/WinSDKLicenseURL"),
    ];
}
