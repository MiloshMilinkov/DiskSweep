using System.Windows;

namespace DiskSweep.App.Views;

public partial class CleanupDialog : Window
{
    public CleanupDialog() => InitializeComponent();

    private void OnClean(object sender, RoutedEventArgs e) => DialogResult = true;
}
