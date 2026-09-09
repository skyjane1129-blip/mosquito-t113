using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mosquito.Client;

public sealed class PhotoWindow : Window
{
    public PhotoWindow(System.Windows.Media.Imaging.BitmapSource source)
    {
        Title = "采集影像 · 原图查看"; Width = 1100; Height = 760; MinWidth = 600; MinHeight = 400;
        Background = (Brush)Application.Current.FindResource("PageBrush");
        var root = new DockPanel { Margin = new Thickness(16) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var picture = new Image { Source = source, Stretch = Stretch.Uniform };
        var viewport = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = picture };
        root.Children.Add(viewport); Content = root;
        var scale = 1.0; var fit = true;
        void Resize()
        {
            var actual = fit ? Math.Min(Math.Max(1, viewport.ActualWidth - 24) / source.PixelWidth, Math.Max(1, viewport.ActualHeight - 24) / source.PixelHeight) : scale;
            picture.Width = source.PixelWidth * actual; picture.Height = source.PixelHeight * actual;
        }
        void Button(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("SecondaryButton") };
            button.Click += (_, _) => { action(); Resize(); }; toolbar.Children.Add(button);
        }
        Button("适应窗口", () => fit = true); Button("原始大小 100%", () => { fit = false; scale = 1; });
        Button("放大 +", () => { if (fit) scale = picture.Width / source.PixelWidth; fit = false; scale = Math.Min(4, scale * 1.25); });
        Button("缩小 −", () => { if (fit) scale = picture.Width / source.PixelWidth; fit = false; scale = Math.Max(.05, scale / 1.25); });
        viewport.SizeChanged += (_, _) => Resize();
    }
}
