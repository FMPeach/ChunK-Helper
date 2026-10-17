using System.Windows;
using System.Windows.Controls;
using PureKBridge.App.Services;

namespace PureKBridge.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Title = "关于" + AboutInfo.Name;
        DataContext = AboutInfo.Version;
    }

    private void OpenLicenses(object sender, RoutedEventArgs e)
    {
        var list = new ListBox { ItemsSource = AboutInfo.Licenses, DisplayMemberPath = nameof(LicenseNotice.Name), MinWidth = 180 };
        var text = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(14, 0, 0, 0) };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is LicenseNotice notice)
                text.Text = notice.Name + " · " + notice.License + "\n\n" + notice.Text;
        };
        list.SelectedIndex = 0;
        var grid = new Grid { Margin = new Thickness(20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(list);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        new Window { Title = "第三方许可证", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 820, Height = 560, MinWidth = 640, MinHeight = 400, Content = grid }.ShowDialog();
    }
}
