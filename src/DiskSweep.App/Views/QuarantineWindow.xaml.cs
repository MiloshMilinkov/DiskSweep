using System.Windows;

namespace DiskSweep.App.Views;

public partial class QuarantineWindow : Window
{
    public QuarantineWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
