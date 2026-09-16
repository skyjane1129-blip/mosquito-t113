using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mosquito.Client.Core;
using Mosquito.Client.Core.Geo;
using Mosquito.Client.Map;

namespace Mosquito.Client;

// Remote-mode map. Overview: the 16 districts drawn from real boundary data in Web Mercator, so shapes and
// proportions are true. District view: a slippy map (raster base tiles, wheel zoom, drag pan) clipped to
// the chosen district, with device markers. All device coordinates are GCJ02.
public sealed class ShanghaiMap : Grid
{
    private const string DistrictAutomationPrefix = "ShanghaiDistrict-";
    private const string DeviceMarkerAutomationId = "ShanghaiDeviceMarker";
    private const string DistrictSelectorAutomationId = "ShanghaiDistrictSelector";
    private const string MapNoteAutomationId = "ShanghaiMapNote";
    private const string ResetAutomationId = "ShanghaiMapReset";
    private const string CanvasAutomationId = "ShanghaiMapCanvas";
    private const string ZoomInAutomationId = "ShanghaiMapZoomIn";
    private const string ZoomOutAutomationId = "ShanghaiMapZoomOut";
    private const string AllDistricts = "上海市";
    private const double OverviewPadding = 16, DistrictPadding = 24, WheelStep = 0.5, ClusterCellWidth = 70, ClusterCellHeight = 40;

    private static readonly DistrictAtlas Atlas = DistrictAtlas.Shanghai;
    private static readonly Brush[] Palette =
    [
        Frozen(Color.FromRgb(214, 230, 239)), Frozen(Color.FromRgb(221, 235, 226)), Frozen(Color.FromRgb(228, 226, 240)),
        Frozen(Color.FromRgb(233, 229, 216)), Frozen(Color.FromRgb(216, 233, 235)), Frozen(Color.FromRgb(236, 224, 226))
    ];
    private static readonly Brush HoverBrush = Frozen(Color.FromRgb(178, 212, 226));
    private static readonly Brush WaterBrush = Frozen(Color.FromRgb(235, 242, 247));
    private static readonly Brush MaskBrush = Frozen(Color.FromArgb(0xB8, 0xE3, 0xEA, 0xEF));
    private static readonly Brush LeaderBrush = Frozen(Color.FromRgb(148, 163, 184));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(51, 65, 85));

    private readonly Canvas _canvas = new() { Background = WaterBrush, ClipToBounds = true };
    private readonly Canvas _tileLayer = new(), _vectorLayer = new(), _markerLayer = new();
    private readonly TextBlock _note = new() { FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _zoomButtons = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 12, 12), Visibility = Visibility.Collapsed };
    private readonly TextBlock _attribution = new() { FontSize = 10, Foreground = Brushes.SlateGray, Background = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), Padding = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Dictionary<(int Z, int X, int Y), Image> _tiles = new();
    private readonly Dictionary<string, IReadOnlyList<GeoPolygon>> _wgs84Shapes = new();
    private MainViewModel? _model;
    private TileProvider _provider = TileProvider.None;
    private TileCache? _tileCache;
    private CancellationTokenSource _tileLoads = new();
    private string _district = AllDistricts;
    private double _zoom = 9, _originX, _originY;
    private Size _lastSize;
    private Point? _dragStart;
    private double _dragOriginX, _dragOriginY;
    private bool _dragMoved;

    public ShanghaiMap()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new());
        RowDefinitions.Add(new() { Height = GridLength.Auto });

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var reset = new Button { Content = "返回上海全图", Style = (Style)Application.Current.FindResource("SecondaryButton"), Padding = new Thickness(10, 6, 10, 6) };
        AutomationProperties.SetAutomationId(reset, ResetAutomationId);
        reset.Click += (_, _) => { if (_model is not null) _model.District = AllDistricts; };
        DockPanel.SetDock(reset, Dock.Right);
        toolbar.Children.Add(reset);
        var selector = new ComboBox
        {
            Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 12, 0),
            ItemsSource = new[] { AllDistricts }.Concat(Atlas.Districts.Select(x => x.Name)).ToArray()
        };
        AutomationProperties.SetAutomationId(selector, DistrictSelectorAutomationId);
        selector.SetBinding(ComboBox.SelectedItemProperty, new System.Windows.Data.Binding("District") { Mode = System.Windows.Data.BindingMode.TwoWay });
        toolbar.Children.Add(selector);
        Children.Add(toolbar);

        _canvas.Children.Add(_tileLayer); _canvas.Children.Add(_vectorLayer); _canvas.Children.Add(_markerLayer);
        AutomationProperties.SetAutomationId(_canvas, CanvasAutomationId);
        _canvas.SizeChanged += (_, e) => OnCanvasResized(e.PreviousSize, e.NewSize);
        _canvas.MouseWheel += OnWheel;
        _canvas.MouseLeftButtonDown += OnMouseDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseUp;
        _canvas.LostMouseCapture += (_, _) => _dragStart = null;

        var zoomIn = ZoomButton("+", ZoomInAutomationId, () => ZoomAt(_zoom + 1, ViewCenter));
        var zoomOut = ZoomButton("−", ZoomOutAutomationId, () => ZoomAt(_zoom - 1, ViewCenter));
        zoomOut.Margin = new Thickness(0, 6, 0, 0);
        _zoomButtons.Children.Add(zoomIn); _zoomButtons.Children.Add(zoomOut);
        var host = new Grid();
        host.Children.Add(_canvas); host.Children.Add(_attribution); host.Children.Add(_zoomButtons);
        SetRow(host, 1);
        Children.Add(host);

        SetRow(_note, 2);
        AutomationProperties.SetAutomationId(_note, MapNoteAutomationId);
        Children.Add(_note);

        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        // The map lives in a TabItem: leaving the tab unloads it (drop pending tile downloads), returning reloads it.
        Unloaded += (_, _) => CancelTileLoads();
        Loaded += (_, _) => Redraw();
    }

    // Diagnostics for tests and the note line.
    public double Zoom => _zoom;
    public bool IsTileMode => _district != AllDistricts;
    public int VisibleTileCount => _tiles.Count;
    public string TileAttribution => _provider.Attribution;
    public string District => _district;

    public static bool InDistrict(string district, GeoSample location) =>
        location.IsValid && Atlas.InDistrict(district, location.Latitude, location.Longitude);

    private Point ViewCenter => new(_canvas.ActualWidth / 2, _canvas.ActualHeight / 2);
    private bool Ready => _canvas.ActualWidth >= 20 && _canvas.ActualHeight >= 20;
    private int MinZoom => Math.Max(_model?.MapSettings.MinZoom ?? 10, _provider.MinZoom);
    private int MaxZoom => Math.Min(_model?.MapSettings.MaxZoom ?? 18, _provider.MaxZoom);

    private static SolidColorBrush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    private static Button ZoomButton(string text, string automationId, Action action)
    {
        var button = new Button
        {
            Content = text, Width = 34, Height = 34, Padding = new Thickness(0), FontSize = 18,
            Background = Brushes.White, Foreground = (Brush)Application.Current.FindResource("PrimaryBrush"), Style = (Style)Application.Current.FindResource("SecondaryButton")
        };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => action();
        return button;
    }

    private void Attach(MainViewModel? model)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = model;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
        CancelTileLoads();
        _tileCache?.Dispose(); _tileCache = null;
        _provider = TileProvider.FromSettings(_model?.MapSettings);
        if (_provider.HasTiles && _model is not null) _tileCache = new TileCache(_provider, _model.MapSettings.CacheDirectory);
        _wgs84Shapes.Clear();
        _district = _model?.District ?? AllDistricts;
        Refit();
        Redraw();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.District))
        {
            var district = _model?.District ?? AllDistricts;
            if (district == _district) return;
            _district = Atlas.Find(district) is null ? AllDistricts : district;
            CancelTileLoads();
            Refit();
            Redraw();
        }
        else if (e.PropertyName is nameof(MainViewModel.VisibleDevices) or nameof(MainViewModel.SelectedDevice))
        {
            if (!Ready) return;
            RenderVector();
            RenderMarkers();
        }
    }

    private void OnCanvasResized(Size previous, Size current)
    {
        if (!Ready) return;
        if (!IsTileMode || _lastSize.Width < 20 || _lastSize.Height < 20) Refit();
        else
        {
            // Keep the geographic centre while the window is resized.
            _originX -= (current.Width - previous.Width) / 2;
            _originY -= (current.Height - previous.Height) / 2;
        }
        _lastSize = current;
        Redraw();
    }

    private void Refit()
    {
        if (!Ready) return;
        _lastSize = new Size(_canvas.ActualWidth, _canvas.ActualHeight);
        if (IsTileMode && Atlas.Find(_district) is { } shape)
        {
            var bounds = Adjust(shape.Bounds);
            _zoom = WebMercator.Clamp(WebMercator.FitZoom(bounds, _canvas.ActualWidth, _canvas.ActualHeight, DistrictPadding), MinZoom, MaxZoom);
            CenterOn(bounds.Center);
        }
        else
        {
            _zoom = WebMercator.FitZoom(Atlas.Bounds, _canvas.ActualWidth, _canvas.ActualHeight, OverviewPadding);
            CenterOn(Atlas.Bounds.Center);
        }
    }

    private void CenterOn(GeoPoint point)
    {
        var (x, y) = WebMercator.ToWorld(point.Longitude, point.Latitude, _zoom);
        _originX = x - _canvas.ActualWidth / 2;
        _originY = y - _canvas.ActualHeight / 2;
    }

    // Base tiles may be WGS84 (OpenStreetMap); everything else in the app is GCJ02.
    private bool UsesWgs84 => IsTileMode && _provider.HasTiles && _provider.CoordinateSystem == "WGS84";
    private GeoPoint Adjust(GeoPoint point)
    {
        if (!UsesWgs84) return point;
        var (lon, lat) = ChinaCoordinates.Gcj02ToWgs84(point.Longitude, point.Latitude);
        return new GeoPoint(lon, lat);
    }
    private GeoBounds Adjust(GeoBounds bounds) => UsesWgs84
        ? GeoBounds.Of([Adjust(new GeoPoint(bounds.MinLongitude, bounds.MinLatitude)), Adjust(new GeoPoint(bounds.MaxLongitude, bounds.MaxLatitude))])
        : bounds;
    private IReadOnlyList<GeoPolygon> ShapePolygons(DistrictShape shape)
    {
        if (!UsesWgs84) return shape.Polygons;
        if (!_wgs84Shapes.TryGetValue(shape.Name, out var converted))
        {
            converted = shape.Polygons.Select(p => new GeoPolygon(Convert(p.Outer), p.Holes.Select(Convert).ToArray())).ToArray();
            _wgs84Shapes[shape.Name] = converted;
        }
        return converted;
        GeoRing Convert(GeoRing ring) => new(ring.Points.Select(Adjust).ToArray());
    }

    private Point Screen(GeoPoint point)
    {
        var adjusted = Adjust(point);
        var (x, y) = WebMercator.ToWorld(adjusted.Longitude, adjusted.Latitude, _zoom);
        return new Point(x - _originX, y - _originY);
    }
    private Point ScreenProjected(GeoPoint alreadyAdjusted)
    {
        var (x, y) = WebMercator.ToWorld(alreadyAdjusted.Longitude, alreadyAdjusted.Latitude, _zoom);
        return new Point(x - _originX, y - _originY);
    }

    private void Redraw()
    {
        if (!Ready) return;
        RenderVector();
        RenderMarkers();
        RenderTiles();
        _zoomButtons.Visibility = IsTileMode ? Visibility.Visible : Visibility.Collapsed;
        _attribution.Visibility = IsTileMode && _provider.HasTiles ? Visibility.Visible : Visibility.Collapsed;
        _attribution.Text = _provider.Attribution;
        _note.Text = IsTileMode
            ? $"{_district} · 滚轮缩放、拖动平移、双击放大 · {_provider.Attribution} · 设备位置取上次 GCJ02 定位样本（基站定位精度较粗）"
            : $"行政区按真实边界与比例绘制 · {Atlas.Attribution} · 点击行政区进入可缩放地图；未定位或坐标系待确认的设备请从右侧编号列表选择";
    }

    private StreamGeometry BuildGeometry(IReadOnlyList<GeoPolygon> polygons, bool includeViewRectangle)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = geometry.Open())
        {
            if (includeViewRectangle)
            {
                context.BeginFigure(new Point(-1, -1), true, true);
                context.PolyLineTo([new Point(_canvas.ActualWidth + 1, -1), new Point(_canvas.ActualWidth + 1, _canvas.ActualHeight + 1), new Point(-1, _canvas.ActualHeight + 1)], false, false);
            }
            foreach (var polygon in polygons)
            {
                AddRing(context, polygon.Outer);
                foreach (var hole in polygon.Holes) AddRing(context, hole);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void AddRing(StreamGeometryContext context, GeoRing ring)
    {
        if (ring.Points.Length < 3) return;
        context.BeginFigure(ScreenProjected(ring.Points[0]), true, true);
        var rest = new Point[ring.Points.Length - 1];
        for (var i = 1; i < ring.Points.Length; i++) rest[i - 1] = ScreenProjected(ring.Points[i]);
        context.PolyLineTo(rest, true, false);
    }

    private int CountDevices(string district) => _model is null ? 0 : _model.Devices.Count(x =>
        x.LastLocation is { IsValid: true } location && (location.District == district || Atlas.InDistrict(district, location.Latitude, location.Longitude)));

    private void RenderVector()
    {
        _vectorLayer.Children.Clear();
        if (_model is null) return;
        if (IsTileMode)
        {
            if (Atlas.Find(_district) is not { } shape) return;
            var polygons = ShapePolygons(shape);
            _vectorLayer.Children.Add(new System.Windows.Shapes.Path { Data = BuildGeometry(polygons, true), Fill = MaskBrush, IsHitTestVisible = false });
            var outline = new System.Windows.Shapes.Path
            {
                Data = BuildGeometry(polygons, false), Stroke = (Brush)Application.Current.FindResource("AccentBrush"), StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false
            };
            AutomationProperties.SetAutomationId(outline, DistrictAutomationPrefix + shape.Name);
            _vectorLayer.Children.Add(outline);
            return;
        }
        var labels = new List<UIElement>();
        var callouts = new List<(string Text, Point Anchor)>();
        for (var i = 0; i < Atlas.Districts.Count; i++)
        {
            var district = Atlas.Districts[i];
            var count = CountDevices(district.Name);
            var countText = _model.RegistryReady ? $"{count} 台" : "待确认";
            var fill = Palette[i % Palette.Length];
            var path = new System.Windows.Shapes.Path
            {
                Data = BuildGeometry(district.Polygons, false), Fill = fill, Stroke = Brushes.White, StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round, ToolTip = $"{district.Name} · {countText}", Cursor = Cursors.Hand
            };
            AutomationProperties.SetAutomationId(path, DistrictAutomationPrefix + district.Name);
            path.MouseEnter += (_, _) => path.Fill = HoverBrush;
            path.MouseLeave += (_, _) => path.Fill = fill;
            path.MouseLeftButtonDown += (_, e) => { e.Handled = true; _model.District = district.Name; };
            _vectorLayer.Children.Add(path);

            var topLeft = Screen(new GeoPoint(district.Bounds.MinLongitude, district.Bounds.MaxLatitude));
            var bottomRight = Screen(new GeoPoint(district.Bounds.MaxLongitude, district.Bounds.MinLatitude));
            var text = $"{district.Name} · {countText}";
            var centre = Screen(district.Centroid);
            if (bottomRight.X - topLeft.X < 78) { callouts.Add((text, centre)); continue; }
            var label = new TextBlock { Text = text, FontSize = 12, Foreground = LabelBrush, IsHitTestVisible = false };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, centre.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, centre.Y - label.DesiredSize.Height / 2);
            labels.Add(label);
        }
        foreach (var label in labels) _vectorLayer.Children.Add(label);
        RenderCallouts(callouts);
    }

    // The central districts are only a few kilometres wide, so their labels are listed beside the map with leader lines.
    private void RenderCallouts(List<(string Text, Point Anchor)> callouts)
    {
        if (callouts.Count == 0) return;
        const double rowHeight = 20, columnWidth = 118;
        var cityRight = Screen(new GeoPoint(Atlas.Bounds.MaxLongitude, Atlas.Bounds.Center.Latitude)).X;
        var cityLeft = Screen(new GeoPoint(Atlas.Bounds.MinLongitude, Atlas.Bounds.Center.Latitude)).X;
        var rightRoom = _canvas.ActualWidth - cityRight;
        var leftRoom = cityLeft;
        double columnLeft; bool onRight;
        if (rightRoom >= columnWidth + 20) { columnLeft = Math.Min(cityRight + 28, _canvas.ActualWidth - columnWidth - 8); onRight = true; }
        else if (leftRoom >= columnWidth + 20) { columnLeft = Math.Max(8, cityLeft - 28 - columnWidth); onRight = false; }
        else
        {
            // No margin left: fall back to short in-place labels.
            foreach (var (text, anchor) in callouts)
            {
                var label = new TextBlock { Text = text[..text.IndexOf('区')], FontSize = 9.5, Foreground = LabelBrush, IsHitTestVisible = false };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, anchor.X - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, anchor.Y - label.DesiredSize.Height / 2);
                _vectorLayer.Children.Add(label);
            }
            return;
        }
        var ordered = callouts.OrderBy(x => x.Anchor.Y).ToArray();
        var top = Math.Clamp(ordered.Average(x => x.Anchor.Y) - ordered.Length * rowHeight / 2, 4, Math.Max(4, _canvas.ActualHeight - ordered.Length * rowHeight - 4));
        for (var i = 0; i < ordered.Length; i++)
        {
            var (text, anchor) = ordered[i];
            var rowCentre = top + i * rowHeight + rowHeight / 2;
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = LeaderBrush, IsHitTestVisible = false };
            Canvas.SetLeft(dot, anchor.X - 2.5); Canvas.SetTop(dot, anchor.Y - 2.5);
            _vectorLayer.Children.Add(dot);
            var lineEnd = onRight ? columnLeft - 6 : columnLeft + columnWidth + 6;
            _vectorLayer.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = anchor.X, Y1 = anchor.Y, X2 = lineEnd, Y2 = rowCentre, Stroke = LeaderBrush, StrokeThickness = 0.8, IsHitTestVisible = false
            });
            var label = new TextBlock { Text = text, FontSize = 11, Foreground = LabelBrush, IsHitTestVisible = false, Width = columnWidth, TextAlignment = onRight ? TextAlignment.Left : TextAlignment.Right };
            Canvas.SetLeft(label, columnLeft); Canvas.SetTop(label, rowCentre - 8);
            _vectorLayer.Children.Add(label);
        }
    }

    private void RenderMarkers()
    {
        _markerLayer.Children.Clear();
        if (_model is null) return;
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        var primary = (Brush)Application.Current.FindResource("PrimaryBrush");
        var devices = _model.Devices.Where(x => x.LastLocation is { IsValid: true, CoordinateSystem: "GCJ02" })
            .Select(device => (Device: device, Pixel: Screen(new GeoPoint(device.LastLocation!.Longitude, device.LastLocation.Latitude))))
            .Where(x => x.Pixel.X >= -40 && x.Pixel.X <= _canvas.ActualWidth + 40 && x.Pixel.Y >= -20 && x.Pixel.Y <= _canvas.ActualHeight + 20)
            .ToArray();
        if (IsTileMode)
        {
            foreach (var (device, pixel) in devices)
            {
                var inside = device.LastLocation!.District == _district || Atlas.InDistrict(_district, device.LastLocation.Latitude, device.LastLocation.Longitude);
                var selected = ReferenceEquals(device, _model.SelectedDevice) || device.DeviceId == _model.SelectedDevice?.DeviceId;
                var button = Marker(device.DeviceId, selected ? primary : accent, device.DeviceId + (inside ? "" : "（不在本区）"));
                button.Opacity = inside ? 1 : 0.4;
                button.Click += (_, _) => _model.SelectedDevice = device;
                Place(button, pixel);
            }
            return;
        }
        foreach (var group in devices.GroupBy(x => ((int)Math.Floor(x.Pixel.X / ClusterCellWidth), (int)Math.Floor(x.Pixel.Y / ClusterCellHeight))))
        {
            var members = group.ToArray();
            var first = members[0];
            var single = members.Length == 1;
            var button = Marker(single ? first.Device.DeviceId : $"聚合 · {members.Length} 台", single ? accent : primary, string.Join("\n", members.Select(x => x.Device.DeviceId)));
            button.Click += (_, _) =>
            {
                if (single) { _model.SelectedDevice = first.Device; return; }
                var menu = new ContextMenu();
                foreach (var (device, _) in members)
                {
                    var item = new MenuItem { Header = device.DeviceId };
                    item.Click += (_, _) => _model.SelectedDevice = device;
                    menu.Items.Add(item);
                }
                button.ContextMenu = menu;
                menu.IsOpen = true;
            };
            Place(button, first.Pixel);
        }
    }

    private static Button Marker(string text, Brush background, string tooltip)
    {
        var button = new Button { Content = text, FontSize = 11, Padding = new Thickness(8, 5, 8, 5), Background = background, Foreground = Brushes.White, ToolTip = tooltip };
        AutomationProperties.SetAutomationId(button, DeviceMarkerAutomationId);
        return button;
    }

    private void Place(Button button, Point pixel)
    {
        Canvas.SetLeft(button, Math.Clamp(pixel.X - 35, 0, Math.Max(0, _canvas.ActualWidth - 100)));
        Canvas.SetTop(button, Math.Clamp(pixel.Y - 12, 0, Math.Max(0, _canvas.ActualHeight - 30)));
        _markerLayer.Children.Add(button);
    }

    private void RenderTiles()
    {
        if (!IsTileMode || _tileCache is null || !_provider.HasTiles)
        {
            if (_tiles.Count > 0) { _tiles.Clear(); _tileLayer.Children.Clear(); }
            return;
        }
        var level = Math.Clamp((int)Math.Round(_zoom), _provider.MinZoom, _provider.MaxZoom);
        var scale = Math.Pow(2, _zoom - level);
        var tilePixels = WebMercator.TileSize * scale;
        var worldTiles = 1 << level;
        var firstX = Math.Max(0, (int)Math.Floor(_originX / tilePixels) - 1);
        var lastX = Math.Min(worldTiles - 1, (int)Math.Floor((_originX + _canvas.ActualWidth) / tilePixels) + 1);
        var firstY = Math.Max(0, (int)Math.Floor(_originY / tilePixels) - 1);
        var lastY = Math.Min(worldTiles - 1, (int)Math.Floor((_originY + _canvas.ActualHeight) / tilePixels) + 1);
        var wanted = new HashSet<(int Z, int X, int Y)>();
        for (var x = firstX; x <= lastX; x++)
            for (var y = firstY; y <= lastY; y++) wanted.Add((level, x, y));
        foreach (var key in _tiles.Keys.Where(key => !wanted.Contains(key)).ToArray())
        {
            _tileLayer.Children.Remove(_tiles[key]);
            _tiles.Remove(key);
        }
        var token = _tileLoads.Token;
        foreach (var key in wanted)
        {
            if (!_tiles.TryGetValue(key, out var image))
            {
                image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                _tiles[key] = image;
                _tileLayer.Children.Add(image);
                _ = LoadTileAsync(key, image, token);
            }
            image.Width = tilePixels + 0.5; image.Height = tilePixels + 0.5;
            Canvas.SetLeft(image, key.X * tilePixels - _originX);
            Canvas.SetTop(image, key.Y * tilePixels - _originY);
        }
    }

    private async Task LoadTileAsync((int Z, int X, int Y) key, Image image, CancellationToken token)
    {
        if (_tileCache is null) return;
        try
        {
            var bitmap = await _tileCache.GetTileAsync(key.Z, key.X, key.Y, token);
            if (bitmap is not null && !token.IsCancellationRequested && _tiles.TryGetValue(key, out var current) && ReferenceEquals(current, image))
                image.Source = bitmap;
        }
        catch (OperationCanceledException) { }
    }

    private void CancelTileLoads()
    {
        _tileLoads.Cancel();
        _tileLoads.Dispose();
        _tileLoads = new CancellationTokenSource();
        _tiles.Clear();
        _tileLayer.Children.Clear();
    }

    private void ZoomAt(double zoom, Point anchor)
    {
        if (!IsTileMode || !Ready) return;
        zoom = WebMercator.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 1e-9) return;
        var factor = Math.Pow(2, zoom - _zoom);
        _originX = (_originX + anchor.X) * factor - anchor.X;
        _originY = (_originY + anchor.Y) * factor - anchor.Y;
        _zoom = zoom;
        Redraw();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsTileMode) return;
        ZoomAt(_zoom + Math.Sign(e.Delta) * WheelStep, Anchor(e.GetPosition(_canvas)));
        e.Handled = true;
    }

    // Zoom about the pointer when it is over the map; otherwise (synthetic events, off-canvas pointer) about the centre.
    private Point Anchor(Point position) =>
        position.X >= 0 && position.Y >= 0 && position.X <= _canvas.ActualWidth && position.Y <= _canvas.ActualHeight ? position : ViewCenter;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsTileMode) return;
        var position = e.GetPosition(_canvas);
        if (e.ClickCount == 2) { ZoomAt(_zoom + 1, Anchor(position)); e.Handled = true; return; }
        _dragStart = position; _dragOriginX = _originX; _dragOriginY = _originY; _dragMoved = false;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(_canvas);
        var dx = position.X - start.X; var dy = position.Y - start.Y;
        if (!_dragMoved && Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
        _dragMoved = true;
        _originX = _dragOriginX - dx; _originY = _dragOriginY - dy;
        Redraw();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        _canvas.ReleaseMouseCapture();
        e.Handled = _dragMoved;
    }
}
