using System.Windows;
using System.Windows.Controls;
namespace Mosquito.Client;
public partial class PhotoPane : UserControl
{
    public PhotoPane() => InitializeComponent();
    public static readonly DependencyProperty PreviewHeightProperty = DependencyProperty.Register(nameof(PreviewHeight), typeof(GridLength), typeof(PhotoPane), new PropertyMetadata(new GridLength(180)));
    public GridLength PreviewHeight { get => (GridLength)GetValue(PreviewHeightProperty); set => SetValue(PreviewHeightProperty, value); }
    private void OpenPhoto(object sender, RoutedEventArgs e) { if (DataContext is PhotoViewModel photo) photo.OpenViewer(); }
    private async void SavePhoto(object sender, RoutedEventArgs e) { if (DataContext is PhotoViewModel photo) await photo.SaveAsync(); }
}
