using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Mosquito.Client.Core;

namespace Mosquito.Client;

public sealed class ShanghaiMap : Grid
{
    private const string DistrictAutomationPrefix = "ShanghaiDistrict-";
    private const string DeviceMarkerAutomationId = "ShanghaiDeviceMarker";
    private const string DistrictSelectorAutomationId = "ShanghaiDistrictSelector";
    private const string MapNoteAutomationId = "ShanghaiMapNote";
    private const string ResetAutomationId = "ShanghaiMapReset";

    private sealed record DistrictShape(string Name, Point Center, Point[][] Rings);

    // Project-authored navigation diagram. These deliberately coarse polygons were not
    // traced, copied or simplified from a map dataset and are not administrative boundaries.
    private static readonly IReadOnlyList<DistrictShape> DistrictShapes =
    [
        District("黄浦区", 121.48, 31.21,
            (121.45, 31.25), (121.49, 31.25), (121.51, 31.20), (121.49, 31.16), (121.45, 31.18)),
        District("徐汇区", 121.42, 31.14,
            (121.37, 31.19), (121.45, 31.19), (121.48, 31.10), (121.43, 31.05), (121.37, 31.09)),
        District("长宁区", 121.37, 31.22,
            (121.32, 31.25), (121.42, 31.25), (121.45, 31.19), (121.37, 31.18), (121.31, 31.21)),
        District("静安区", 121.45, 31.28,
            (121.41, 31.33), (121.48, 31.33), (121.50, 31.25), (121.42, 31.25)),
        District("普陀区", 121.37, 31.29,
            (121.29, 31.33), (121.41, 31.33), (121.42, 31.25), (121.32, 31.25)),
        District("虹口区", 121.50, 31.29,
            (121.48, 31.34), (121.54, 31.34), (121.55, 31.26), (121.50, 31.25)),
        District("杨浦区", 121.59, 31.31,
            (121.54, 31.39), (121.65, 31.39), (121.68, 31.30), (121.61, 31.24), (121.54, 31.26)),
        District("闵行区", 121.32, 31.08,
            (121.18, 31.23), (121.31, 31.23), (121.37, 31.18), (121.37, 31.09),
            (121.43, 31.05), (121.50, 30.98), (121.25, 30.95), (121.17, 31.08)),
        District("宝山区", 121.40, 31.42,
            (121.29, 31.52), (121.51, 31.52), (121.55, 31.35), (121.48, 31.33), (121.34, 31.33)),
        District("嘉定区", 121.20, 31.39,
            (121.05, 31.51), (121.29, 31.52), (121.34, 31.33), (121.28, 31.25), (121.09, 31.26)),
        District("浦东新区", 121.74, 31.12,
            (121.54, 31.39), (121.68, 31.42), (121.95, 31.34), (122.02, 31.12),
            (121.90, 30.84), (121.62, 30.80), (121.50, 30.99), (121.51, 31.17), (121.54, 31.25)),
        District("金山区", 121.17, 30.78,
            (120.96, 30.91), (121.32, 30.92), (121.38, 30.73), (121.26, 30.64), (121.02, 30.67)),
        District("松江区", 121.18, 30.97,
            (121.00, 31.08), (121.18, 31.08), (121.25, 30.95), (121.36, 30.92), (121.31, 30.83), (121.05, 30.85)),
        District("青浦区", 121.03, 31.18,
            (120.86, 31.31), (121.09, 31.32), (121.18, 31.23), (121.17, 31.08), (121.00, 31.08), (120.86, 31.16)),
        District("奉贤区", 121.57, 30.84,
            (121.36, 30.92), (121.50, 30.98), (121.75, 30.98), (121.76, 30.76), (121.62, 30.67), (121.38, 30.73)),
        District("崇明区", 121.63, 31.68,
            (121.18, 31.67), (121.35, 31.82), (121.70, 31.88), (122.04, 31.72), (121.91, 31.52), (121.55, 31.50))
    ];

    private readonly Canvas _canvas = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(235, 242, 247)),
        ClipToBounds = true
    };
    private readonly TextBlock _note = new()
    {
        FontSize = 11,
        Foreground = Brushes.SlateGray,
        TextWrapping = TextWrapping.Wrap
    };
    private MainViewModel? _model;
    private Rect? _zoom;

    public ShanghaiMap()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new());
        RowDefinitions.Add(new() { Height = GridLength.Auto });

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var reset = new Button
        {
            Content = "返回上海全图",
            Style = (Style)Application.Current.FindResource("SecondaryButton"),
            Padding = new Thickness(10, 6, 10, 6)
        };
        AutomationProperties.SetAutomationId(reset, ResetAutomationId);
        reset.Click += (_, _) =>
        {
            _zoom = null;
            if (_model is not null) _model.District = "上海市";
            Draw();
        };
        DockPanel.SetDock(reset, Dock.Right);
        toolbar.Children.Add(reset);

        var selector = new ComboBox
        {
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 12, 0),
            ItemsSource = new[] { "上海市" }.Concat(DistrictShapes.Select(x => x.Name))
        };
        AutomationProperties.SetAutomationId(selector, DistrictSelectorAutomationId);
        selector.SetBinding(ComboBox.SelectedItemProperty,
            new System.Windows.Data.Binding("District")
            {
                Mode = System.Windows.Data.BindingMode.TwoWay
            });
        toolbar.Children.Add(selector);
        Children.Add(toolbar);

        SetRow(_canvas, 1);
        Children.Add(_canvas);
        SetRow(_note, 2);
        _note.Margin = new(0, 8, 0, 0);
        AutomationProperties.SetAutomationId(_note, MapNoteAutomationId);
        Children.Add(_note);

        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= Changed;
            _model = DataContext as MainViewModel;
            if (_model is not null) _model.PropertyChanged += Changed;
            Draw();
        };
        _canvas.SizeChanged += (_, _) => Draw();
    }

    private static DistrictShape District(
        string name,
        double centerLongitude,
        double centerLatitude,
        params (double Longitude, double Latitude)[] boundary)
    {
        Point ToPoint((double Longitude, double Latitude) coordinate) =>
            new(coordinate.Longitude, -coordinate.Latitude);

        return new DistrictShape(
            name,
            new Point(centerLongitude, -centerLatitude),
            [boundary.Select(ToPoint).ToArray()]);
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
        var shapes = DistrictShapes.Where(x => _model.District == "上海市" || x.Name == _model.District).ToArray();
        var points = shapes.SelectMany(x => x.Rings).SelectMany(x => x).ToArray();
        if (points.Length == 0) return;
        var bounds = _zoom ?? new Rect(
            new Point(points.Min(p => p.X), points.Min(p => p.Y)),
            new Point(points.Max(p => p.X), points.Max(p => p.Y)));
        var scale = Math.Min((_canvas.ActualWidth - 40) / bounds.Width, (_canvas.ActualHeight - 40) / bounds.Height);
        Point Project(Point p) => new(
            (p.X - bounds.X - bounds.Width / 2) * scale + _canvas.ActualWidth / 2,
            (p.Y - bounds.Y - bounds.Height / 2) * scale + _canvas.ActualHeight / 2);

        foreach (var district in shapes)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                foreach (var ring in district.Rings)
                {
                    context.BeginFigure(Project(ring[0]), true, true);
                    context.PolyLineTo(ring.Skip(1).Select(Project).ToArray(), true, false);
                }
            }
            var path = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(Color.FromRgb(215, 231, 237)),
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                ToolTip = district.Name,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            AutomationProperties.SetAutomationId(path, DistrictAutomationPrefix + district.Name);
            path.MouseLeftButtonDown += (_, _) => _model.District = district.Name;
            _canvas.Children.Add(path);

            if (_model.District != "上海市" || district.Name is "浦东新区" or "崇明区" or "青浦区" or "松江区" or "金山区" or "奉贤区" or "嘉定区" or "宝山区")
            {
                var count = _model.Devices.Count(x => x.LastLocation is { IsValid: true } location && location.District == district.Name);
                var text = new TextBlock
                {
                    Text = district.Name + (_model.RegistryReady ? $" · {count} 台" : " · 待确认"),
                    FontSize = 12,
                    Foreground = Brushes.SlateGray,
                    IsHitTestVisible = false
                };
                var center = Project(district.Center);
                Canvas.SetLeft(text, center.X - 35);
                Canvas.SetTop(text, center.Y);
                _canvas.Children.Add(text);
            }
        }

        var valid = _model.VisibleDevices
            .Where(x => x.LastLocation is { IsValid: true, CoordinateSystem: "GCJ02" })
            .ToArray();
        // Distinct cluster labels, with zoom on click. Coordinates with an unknown datum stay in the device list.
        foreach (var group in valid.GroupBy(x =>
                 {
                     var point = Project(new Point(x.LastLocation!.Longitude, -x.LastLocation.Latitude));
                     return ((int)(point.X / 70), (int)(point.Y / 40));
                 }))
        {
            var devices = group.ToArray();
            var first = devices[0];
            var location = first.LastLocation!;
            var point = new Point(location.Longitude, -location.Latitude);
            var pixel = Project(point);
            if (!bounds.Contains(point) || pixel.X < 0 || pixel.X > _canvas.ActualWidth || pixel.Y < 0 || pixel.Y > _canvas.ActualHeight) continue;
            var button = new Button
            {
                Content = devices.Length == 1 ? first.DeviceId : $"聚合 · {devices.Length} 台",
                FontSize = 11,
                Padding = new Thickness(8, 5, 8, 5),
                Background = devices.Length == 1
                    ? (Brush)Application.Current.FindResource("AccentBrush")
                    : (Brush)Application.Current.FindResource("PrimaryBrush"),
                Foreground = Brushes.White,
                ToolTip = string.Join("\n", devices.Select(x => x.DeviceId))
            };
            AutomationProperties.SetAutomationId(button, DeviceMarkerAutomationId);
            button.Click += (_, _) =>
            {
                if (devices.Length == 1)
                {
                    _model.SelectedDevice = first;
                }
                else if (bounds.Width < .0001 || devices.All(x =>
                             x.LastLocation!.Longitude == location.Longitude &&
                             x.LastLocation.Latitude == location.Latitude))
                {
                    var menu = new ContextMenu();
                    foreach (var device in devices)
                    {
                        var item = new MenuItem { Header = device.DeviceId };
                        item.Click += (_, _) => _model.SelectedDevice = device;
                        menu.Items.Add(item);
                    }
                    button.ContextMenu = menu;
                    menu.IsOpen = true;
                }
                else
                {
                    _zoom = new Rect(
                        point.X - bounds.Width / 4,
                        point.Y - bounds.Height / 4,
                        bounds.Width / 2,
                        bounds.Height / 2);
                    Draw();
                }
            };
            Canvas.SetLeft(button, Math.Clamp(pixel.X - 35, 0, Math.Max(0, _canvas.ActualWidth - 100)));
            Canvas.SetTop(button, Math.Clamp(pixel.Y - 12, 0, _canvas.ActualHeight - 30));
            _canvas.Children.Add(button);
        }

        _note.Text = $"{_model.District} · 点击行政区放大，再选择设备。未定位、坐标系待确认的设备请从右侧编号列表选择。\n" +
                     "项目自绘行政区示意图，不代表精确边界；设备位置取上次 GCJ02 GNSS 样本。";
    }
}
