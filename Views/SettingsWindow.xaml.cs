using System.Windows;
using BookHub.Reader.ViewModels;

namespace BookHub.Reader.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(ReaderViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
