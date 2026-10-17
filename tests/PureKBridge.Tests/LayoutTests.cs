using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using PureKBridge.App.ViewModels;
using PureKBridge.App.Views;

namespace PureKBridge.Tests;

internal sealed class LayoutTests
{
    private readonly MainViewModel _model;
    private readonly Action<bool, string> _check;
    private readonly TraceErrors _errors = new();

    public LayoutTests(string artifacts, MainViewModel model, Action<bool, string> check)
    {
        _model = model; _check = check;
        var source = Path.GetFullPath(Path.Combine(artifacts, "../../src/PureKBridge.App/App.xaml"));
        var application = XDocument.Load(source);
        var dictionary = new XElement(XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            application.Root!.Elements().Single().Elements().Select(element => new XElement(element)));
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(dictionary.ToString()));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(_errors);
    }

    public async Task CheckPageAsync(string name)
    {
        UserControl page = name switch
        {
            "识别房间" => new ScanView(), "微信授权" => new WechatView(),
            "连接设备" => new ConnectView(), _ => new ConnectedView()
        };
        page.DataContext = _model;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        foreach (var width in new[] { 550.0, 660.0 })
        {
            page.Measure(new Size(width, double.PositiveInfinity));
            page.Arrange(new Rect(0, 0, width, page.DesiredSize.Height));
            page.UpdateLayout();
            _check(page.DesiredSize.Width <= width + 1 && page.ActualHeight > 0 && _errors.Messages.Count == 0,
                $"{name}页面在内容宽度 {width} 的布局及绑定无错误");
        }
    }

    public void Finish() => PresentationTraceSources.DataBindingSource.Listeners.Remove(_errors);

    private sealed class TraceErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
