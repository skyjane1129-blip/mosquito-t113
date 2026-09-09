using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Mosquito.Client.Core;

namespace Mosquito.Client;

public sealed class ShanghaiMap : Grid
{
    private sealed record DistrictShape(string Name, Point Center, Point[][] Rings);
    private readonly List<DistrictShape> _districts = [];
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromRgb(235, 242, 247)), ClipToBounds = true };
    private readonly TextBlock _note = new() { FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap };
    private MainViewModel? _model;
    private Rect? _zoom;
    public ShanghaiMap()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new()); RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var reset = new Button { Content = "返回上海全图", Style = (Style)Application.Current.FindResource("SecondaryButton"), Padding = new Thickness(10, 6, 10, 6) };
        reset.Click += (_, _) => { _zoom = null; if (_model is not null) _model.District = "上海市"; Draw(); };
        DockPanel.SetDock(reset, Dock.Right); toolbar.Children.Add(reset);
        var selector = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 12, 0) };
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/MosquitoCapture;component/Assets/shanghai-districts.json")).Stream;
        using var json = JsonDocument.Parse(stream);
        foreach (var feature in json.RootElement.GetProperty("features").EnumerateArray())
        {
            var properties = feature.GetProperty("properties"); var center = properties.GetProperty("centroid");
            var rings = feature.GetProperty("geometry").GetProperty("coordinates").EnumerateArray()
                .SelectMany(polygon => polygon.EnumerateArray()).Select(ring => ring.EnumerateArray().Select(p => new Point(p[0].GetDouble(), -p[1].GetDouble())).ToArray()).ToArray();
            _districts.Add(new(properties.GetProperty("name").GetString()!, new(center[0].GetDouble(), -center[1].GetDouble()), rings));
        }
        selector.ItemsSource = new[] { "上海市" }.Concat(_districts.Select(x => x.Name));
        selector.SetBinding(ComboBox.SelectedItemProperty, new System.Windows.Data.Binding("District") { Mode = System.Windows.Data.BindingMode.TwoWay });
        toolbar.Children.Add(selector); Children.Add(toolbar); SetRow(_canvas, 1); Children.Add(_canvas); SetRow(_note, 2); _note.Margin = new(0, 8, 0, 0); Children.Add(_note);
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= Changed;
            _model = DataContext as MainViewModel;
            if (_model is not null) _model.PropertyChanged += Changed;
            Draw();
        };
        _canvas.SizeChanged += (_, _) => Draw();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.District)) _zoom = null;
        if (e.PropertyName is nameof(MainViewModel.District) or nameof(MainViewModel.VisibleDevices) or nameof(MainViewModel.SelectedDevice)) Draw();
    }
    private void Draw()
    {
        if (_canvas.ActualWidth < 20 || _canvas.ActualHeight < 20 || _model is null) return;
        _canvas.Children.Clear();
        var shapes = _districts.Where(x => _model.District == "上海市" || x.Name == _model.District).ToArray();
        var points = shapes.SelectMany(x => x.Rings).SelectMany(x => x).ToArray();
        if (points.Length == 0) return;
        var bounds = _zoom ?? new Rect(new Point(points.Min(p => p.X), points.Min(p => p.Y)), new Point(points.Max(p => p.X), points.Max(p => p.Y)));
        var scale = Math.Min((_canvas.ActualWidth - 40) / bounds.Width, (_canvas.ActualHeight - 40) / bounds.Height);
        Point Project(Point p) => new((p.X - bounds.X - bounds.Width / 2) * scale + _canvas.ActualWidth / 2, (p.Y - bounds.Y - bounds.Height / 2) * scale + _canvas.ActualHeight / 2);
        foreach (var district in shapes)
        {
            var geometry = new StreamGeometry(); using (var context = geometry.Open()) foreach (var ring in district.Rings) { context.BeginFigure(Project(ring[0]), true, true); context.PolyLineTo(ring.Skip(1).Select(Project).ToArray(), true, false); }
            var path = new System.Windows.Shapes.Path { Data = geometry, Fill = new SolidColorBrush(Color.FromRgb(215, 231, 237)), Stroke = Brushes.White, StrokeThickness = 1.5, ToolTip = district.Name, Cursor = System.Windows.Input.Cursors.Hand };
            path.MouseLeftButtonDown += (_, _) => { _model.District = district.Name; }; _canvas.Children.Add(path);
            if (_model.District != "上海市" || district.Name is "浦东新区" or "崇明区" or "青浦区" or "松江区" or "金山区" or "奉贤区" or "嘉定区" or "宝山区")
            {
                var count = _model.Devices.Count(x => x.LastLocation is { IsValid: true } l && l.District == district.Name);
                var text = new TextBlock { Text = district.Name + (_model.RegistryReady ? $" · {count} 台" : " · 待确认"), FontSize = 12, Foreground = Brushes.SlateGray, IsHitTestVisible = false };
                var center = Project(district.Center); Canvas.SetLeft(text, center.X - 35); Canvas.SetTop(text, center.Y); _canvas.Children.Add(text);
            }
        }
        var valid = _model.VisibleDevices.Where(x => x.LastLocation is { IsValid: true, CoordinateSystem: "GCJ02" }).ToArray();
        // Distinct cluster labels, with zoom on click. Coordinates with an unknown datum stay in the device list.
        foreach (var group in valid.GroupBy(x => { var p = Project(new(x.LastLocation!.Longitude, -x.LastLocation.Latitude)); return ((int)(p.X / 70), (int)(p.Y / 40)); }))
        {
            var devices = group.ToArray(); var first = devices[0]; var location = first.LastLocation!; var point = new Point(location.Longitude, -location.Latitude); var pixel = Project(point);
            if (!bounds.Contains(point) || pixel.X < 0 || pixel.X > _canvas.ActualWidth || pixel.Y < 0 || pixel.Y > _canvas.ActualHeight) continue;
            var button = new Button { Content = devices.Length == 1 ? first.DeviceId : $"聚合 · {devices.Length} 台", FontSize = 11, Padding = new Thickness(8, 5, 8, 5), Background = devices.Length == 1 ? (Brush)Application.Current.FindResource("AccentBrush") : (Brush)Application.Current.FindResource("PrimaryBrush"), Foreground = Brushes.White, ToolTip = string.Join("\n", devices.Select(x => x.DeviceId)) };
            button.Click += (_, _) =>
            {
                if (devices.Length == 1) _model.SelectedDevice = first;
                else if (bounds.Width < .0001 || devices.All(x => x.LastLocation!.Longitude == location.Longitude && x.LastLocation.Latitude == location.Latitude))
                {
                    var menu = new ContextMenu();
                    foreach (var device in devices) { var item = new MenuItem { Header = device.DeviceId }; item.Click += (_, _) => _model.SelectedDevice = device; menu.Items.Add(item); }
                    button.ContextMenu = menu; menu.IsOpen = true;
                }
                else { _zoom = new Rect(point.X - bounds.Width / 4, point.Y - bounds.Height / 4, bounds.Width / 2, bounds.Height / 2); Draw(); }
            };
            Canvas.SetLeft(button, Math.Clamp(pixel.X - 35, 0, Math.Max(0, _canvas.ActualWidth - 100))); Canvas.SetTop(button, Math.Clamp(pixel.Y - 12, 0, _canvas.ActualHeight - 30)); _canvas.Children.Add(button);
        }
        _note.Text = $"{_model.District} · 点击行政区放大，再选择设备。未定位、坐标系待确认的设备请从右侧编号列表选择。\n行政区示意底图：DataV GeoAtlas；位置为上次 GNSS 采样，不代表实时位置。";
    }
}
