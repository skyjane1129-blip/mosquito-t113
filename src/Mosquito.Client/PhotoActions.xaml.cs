using System.Windows;
using System.Windows.Controls;

namespace Mosquito.Client;

public partial class PhotoActions : UserControl
{
    public PhotoActions() => InitializeComponent();
    private void OpenPhoto(object sender, RoutedEventArgs e) { if (DataContext is PhotoViewModel photo) photo.OpenViewer(); }
    private async void SavePhoto(object sender, RoutedEventArgs e) { if (DataContext is PhotoViewModel photo) await photo.SaveAsync(); }
}
