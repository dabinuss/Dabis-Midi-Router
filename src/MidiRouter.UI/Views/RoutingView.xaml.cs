using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MidiRouter.UI.ViewModels;

namespace MidiRouter.UI.Views;

public partial class RoutingView : UserControl
{
    private readonly Dictionary<string, Button> _leftConnectorButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _rightConnectorButtons = new(StringComparer.OrdinalIgnoreCase);

    private RoutingViewModel? _viewModel;
    private ScrollViewer? _leftScrollViewer;
    private ScrollViewer? _rightScrollViewer;
    private bool _syncingScroll;
    private bool _renderScheduled;

    private DragCandidate? _dragCandidate;
    private DragState? _dragState;
    private Button? _highlightedTargetButton;
    private ConnectorHit? _pendingClickConnector;
    private bool _suppressNextConnectorClick;

    public RoutingView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LostMouseCapture += OnLostMouseCapture;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focusable = true;
        AttachToViewModel(DataContext as RoutingViewModel);
        HookScrollViewers();
        ScheduleRouteRender();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachToViewModel(e.NewValue as RoutingViewModel);
        HookScrollViewers();
        ScheduleRouteRender();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachFromViewModel();
        ClearConnectorLookups();
        ClearPendingConnector();
        ClearDragState();
    }

    private void AttachToViewModel(RoutingViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        DetachFromViewModel();
        _viewModel = viewModel;

        if (_viewModel is null)
        {
            return;
        }

        _viewModel.EditPortFocusRequested += OnEditPortFocusRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.LeftRoutePorts.CollectionChanged += OnRouteCollectionChanged;
        _viewModel.RightRoutePorts.CollectionChanged += OnRouteCollectionChanged;
        _viewModel.Routes.CollectionChanged += OnRouteCollectionChanged;
    }

    private void DetachFromViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.EditPortFocusRequested -= OnEditPortFocusRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.LeftRoutePorts.CollectionChanged -= OnRouteCollectionChanged;
        _viewModel.RightRoutePorts.CollectionChanged -= OnRouteCollectionChanged;
        _viewModel.Routes.CollectionChanged -= OnRouteCollectionChanged;
        _viewModel = null;
    }

    private void OnEditPortFocusRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            EditPortNameTextBox.Focus();
            Keyboard.Focus(EditPortNameTextBox);
            EditPortNameTextBox.SelectAll();
        }, DispatcherPriority.ContextIdle);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        ScheduleRouteRender();
    }

    private void OnRouteCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HookScrollViewers();
        ScheduleRouteRender();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragState is null)
        {
            return;
        }

        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _ = Mouse.Capture(this, CaptureMode.SubTree);
            return;
        }

        ClearDragState();
        ScheduleRouteRender();
    }

    private void HookScrollViewers()
    {
        var left = FindDescendant<ScrollViewer>(LeftPortsListBox);
        if (!ReferenceEquals(left, _leftScrollViewer))
        {
            if (_leftScrollViewer is not null)
            {
                _leftScrollViewer.ScrollChanged -= OnLeftScrollChanged;
            }

            _leftScrollViewer = left;
            if (_leftScrollViewer is not null)
            {
                _leftScrollViewer.ScrollChanged += OnLeftScrollChanged;
            }
        }

        var right = FindDescendant<ScrollViewer>(RightPortsListBox);
        if (!ReferenceEquals(right, _rightScrollViewer))
        {
            if (_rightScrollViewer is not null)
            {
                _rightScrollViewer.ScrollChanged -= OnRightScrollChanged;
            }

            _rightScrollViewer = right;
            if (_rightScrollViewer is not null)
            {
                _rightScrollViewer.ScrollChanged += OnRightScrollChanged;
            }
        }
    }

    private void OnLeftScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || _rightScrollViewer is null)
        {
            ScheduleRouteRender();
            return;
        }

        _syncingScroll = true;
        _rightScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
        _syncingScroll = false;
        ScheduleRouteRender();
    }

    private void OnRightScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || _leftScrollViewer is null)
        {
            ScheduleRouteRender();
            return;
        }

        _syncingScroll = true;
        _leftScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
        _syncingScroll = false;
        ScheduleRouteRender();
    }

    private void ClearConnectorLookups()
    {
        _leftConnectorButtons.Clear();
        _rightConnectorButtons.Clear();
        _pendingClickConnector = null;
    }

    private void OnConnectorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not RoutingViewModel.RouteEndpointRow row ||
            !TryGetConnectorSide(button.Tag, out var side))
        {
            return;
        }

        button.SizeChanged += OnConnectorSizeChanged;
        if (side == ConnectorSide.Left)
        {
            _leftConnectorButtons[row.Id] = button;
        }
        else
        {
            _rightConnectorButtons[row.Id] = button;
        }

        if (_pendingClickConnector is not null &&
            string.Equals(_pendingClickConnector.EndpointId, row.Id, StringComparison.OrdinalIgnoreCase) &&
            _pendingClickConnector.Side == side)
        {
            _pendingClickConnector = _pendingClickConnector with { Button = button };
            ApplyPendingConnectorStyle(button);
        }

        ScheduleRouteRender();
    }

    private void OnConnectorUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not RoutingViewModel.RouteEndpointRow row ||
            !TryGetConnectorSide(button.Tag, out var side))
        {
            return;
        }

        button.SizeChanged -= OnConnectorSizeChanged;
        if (side == ConnectorSide.Left)
        {
            if (_leftConnectorButtons.TryGetValue(row.Id, out var existing) && ReferenceEquals(existing, button))
            {
                _leftConnectorButtons.Remove(row.Id);
            }
        }
        else
        {
            if (_rightConnectorButtons.TryGetValue(row.Id, out var existing) && ReferenceEquals(existing, button))
            {
                _rightConnectorButtons.Remove(row.Id);
            }
        }

        if (_pendingClickConnector is not null &&
            string.Equals(_pendingClickConnector.EndpointId, row.Id, StringComparison.OrdinalIgnoreCase) &&
            _pendingClickConnector.Side == side)
        {
            _pendingClickConnector = null;
        }

        ScheduleRouteRender();
    }

    private void OnConnectorSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleRouteRender();
    }

    private void OnConnectorPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        if (sender is not Button button ||
            button.DataContext is not RoutingViewModel.RouteEndpointRow row ||
            !TryGetConnectorSide(button.Tag, out var side))
        {
            return;
        }

        SetDragCandidate(row.Id, side, button, e.GetPosition(this));
        _viewModel?.SelectRoutePort(row.Id);
    }

    private void OnRouteItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualAncestor<Button>(source) is not null)
        {
            return;
        }

        Focus();

        if (sender is not Border border ||
            border.DataContext is not RoutingViewModel.RouteEndpointRow row ||
            !TryGetConnectorSide(border.Tag, out var side))
        {
            return;
        }

        if (!TryResolveOriginElement(row.Id, side, border, out var originElement))
        {
            return;
        }

        SetDragCandidate(row.Id, side, originElement, e.GetPosition(this));
        _viewModel?.SelectRoutePort(row.Id);
    }

    private void SetDragCandidate(string endpointId, ConnectorSide side, FrameworkElement originElement, Point startPoint)
    {
        _dragCandidate = new DragCandidate(
            endpointId,
            side,
            originElement,
            startPoint);
    }

    private void OnConnectorClick(object sender, RoutedEventArgs e)
    {
        Focus();

        if (_suppressNextConnectorClick)
        {
            _suppressNextConnectorClick = false;
            return;
        }

        if (sender is not Button button ||
            button.DataContext is not RoutingViewModel.RouteEndpointRow row ||
            !TryGetConnectorSide(button.Tag, out var side))
        {
            return;
        }

        var connector = new ConnectorHit(row.Id, side, button, button);
        _viewModel?.SelectRoutePort(row.Id);

        if (_pendingClickConnector is null)
        {
            SetPendingConnector(connector);
            return;
        }

        if (_pendingClickConnector.Side == connector.Side)
        {
            SetPendingConnector(connector);
            return;
        }

        if (!TryResolveRouteEndpoints(_pendingClickConnector, connector, out var endpoints))
        {
            SetPendingConnector(connector);
            return;
        }

        if (_viewModel is null)
        {
            SetPendingConnector(connector);
            return;
        }

        var canCreate = _viewModel.CanCreateRoute(endpoints.SourceEndpointId, endpoints.TargetEndpointId, out var error);
        if (canCreate)
        {
            _viewModel.TryCreateRouteByDrag(endpoints.SourceEndpointId, endpoints.TargetEndpointId);
            ClearPendingConnector();
            ScheduleRouteRender();
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            _viewModel.ValidationMessage = error;
        }

        SetPendingConnector(connector);
    }

    private void OnRootPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragState is not null)
        {
            if (Mouse.Captured != this)
            {
                _ = Mouse.Capture(this, CaptureMode.SubTree);
            }

            UpdateDrag(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        var distanceX = Math.Abs(point.X - _dragCandidate.StartPoint.X);
        var distanceY = Math.Abs(point.Y - _dragCandidate.StartPoint.Y);
        if (distanceX < 1 && distanceY < 1)
        {
            return;
        }

        StartDrag(_dragCandidate, point);
        e.Handled = true;
    }

    private void OnRootPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragState is not null)
        {
            CompleteDrag(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        _dragCandidate = null;
    }

    private void StartDrag(DragCandidate candidate, Point pointerOnControl)
    {
        _dragCandidate = null;
        _suppressNextConnectorClick = candidate.OriginElement is Button;
        ClearPendingConnector();

        var startPoint = TryGetPortAnchorPoint(candidate.EndpointId, candidate.Side, out var stableStartPoint)
            ? stableStartPoint
            : GetEndpointEdgePoint(candidate.EndpointId, candidate.Side, candidate.OriginElement);
        _dragState = new DragState(candidate.EndpointId, candidate.Side, candidate.OriginElement, startPoint)
        {
            CurrentPoint = startPoint
        };

        candidate.OriginElement.Opacity = 0.85;
        Mouse.OverrideCursor = Cursors.Hand;
        Mouse.Capture(this, CaptureMode.SubTree);
        UpdateDrag(pointerOnControl);
        RenderRoutes();
    }

    private void UpdateDrag(Point pointerOnControl)
    {
        if (_dragState is null)
        {
            return;
        }

        var hit = ResolveConnectorFromVisual(GetHitVisual(pointerOnControl));
        ConnectorHit? validTarget = null;
        if (hit is not null && IsValidDropTarget(_dragState, hit, out _))
        {
            validTarget = hit;
        }
        else if (TryResolveFallbackTarget(pointerOnControl, _dragState, out var fallbackTarget) &&
                 IsValidDropTarget(_dragState, fallbackTarget, out _))
        {
            validTarget = fallbackTarget;
        }

        _dragState.Target = validTarget;
        SetHighlightedTarget(validTarget?.Button);

        _dragState.CurrentPoint = validTarget is not null &&
                                  TryGetPortAnchorPoint(validTarget.EndpointId, validTarget.Side, out var stableTargetPoint)
            ? stableTargetPoint
            : validTarget is not null
                ? GetEndpointEdgePoint(validTarget.EndpointId, validTarget.Side, validTarget.Element)
                : TranslatePoint(pointerOnControl, RoutingCanvas);

        if (IsLoaded)
        {
            RenderRoutes();
        }
        else
        {
            ScheduleRouteRender();
        }
    }

    private void CompleteDrag(Point pointerOnControl)
    {
        if (_dragState is not null)
        {
            UpdateDrag(pointerOnControl);

            var target = _dragState.Target;
            if (target is null &&
                TryResolveFallbackTarget(pointerOnControl, _dragState, out var fallbackTarget))
            {
                target = fallbackTarget;
            }

            if (target is not null &&
                IsValidDropTarget(_dragState, target, out var endpoints))
            {
                _viewModel?.TryCreateRouteByDrag(endpoints.SourceEndpointId, endpoints.TargetEndpointId);
            }
        }

        ClearDragState();
        ScheduleRouteRender();
    }

    private void ClearDragState()
    {
        if (_dragState?.OriginElement is not null)
        {
            _dragState.OriginElement.Opacity = 1;
        }

        _dragCandidate = null;
        _dragState = null;
        SetHighlightedTarget(null);

        if (Mouse.Captured == this)
        {
            Mouse.Capture(null);
        }

        Mouse.OverrideCursor = null;
    }

    private void SetHighlightedTarget(Button? button)
    {
        if (_highlightedTargetButton is not null && !ReferenceEquals(_highlightedTargetButton, button))
        {
            RestoreConnectorStyle(_highlightedTargetButton);
        }

        _highlightedTargetButton = button;
        if (_highlightedTargetButton is null)
        {
            return;
        }

        _highlightedTargetButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
        _highlightedTargetButton.BorderThickness = new Thickness(2);
        _highlightedTargetButton.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE0));
    }

    private void SetPendingConnector(ConnectorHit connector)
    {
        ClearPendingConnector();
        _pendingClickConnector = connector;
        if (connector.Button is not null)
        {
            ApplyPendingConnectorStyle(connector.Button);
        }
    }

    private void ClearPendingConnector()
    {
        if (_pendingClickConnector is not null)
        {
            if (_pendingClickConnector.Button is not null)
            {
                RestoreConnectorStyle(_pendingClickConnector.Button);
            }

            _pendingClickConnector = null;
        }
    }

    private void ApplyPendingConnectorStyle(Button button)
    {
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0xA6, 0x9A));
        button.BorderThickness = new Thickness(2);
        button.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xF2, 0xF1));
    }

    private void RestoreConnectorStyle(Button button)
    {
        if (_pendingClickConnector is not null && ReferenceEquals(_pendingClickConnector.Button, button))
        {
            ApplyPendingConnectorStyle(button);
            return;
        }

        button.ClearValue(Button.BorderBrushProperty);
        button.ClearValue(Button.BorderThicknessProperty);
        button.ClearValue(Button.BackgroundProperty);
    }

    private bool IsValidDropTarget(DragState dragState, ConnectorHit target, out RouteEndpoints endpoints)
    {
        if (!TryResolveRouteEndpoints(new ConnectorHit(dragState.EndpointId, dragState.Side, dragState.OriginElement, dragState.OriginButton), target, out endpoints))
        {
            return false;
        }

        return _viewModel?.CanCreateRoute(endpoints.SourceEndpointId, endpoints.TargetEndpointId, out _) == true;
    }

    private static bool TryResolveRouteEndpoints(ConnectorHit first, ConnectorHit second, out RouteEndpoints endpoints)
    {
        endpoints = default;
        if (first.Side == second.Side)
        {
            return false;
        }

        endpoints = first.Side == ConnectorSide.Left
            ? new RouteEndpoints(first.EndpointId, second.EndpointId)
            : new RouteEndpoints(second.EndpointId, first.EndpointId);

        return true;
    }

    private void ScheduleRouteRender()
    {
        if (!IsLoaded || _renderScheduled)
        {
            return;
        }

        _renderScheduled = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            _renderScheduled = false;
            RenderRoutes();
        }, DispatcherPriority.Render);
    }

    private void RenderRoutes()
    {
        RoutingCanvas.Children.Clear();
        DragPreviewCanvas.Children.Clear();

        if (_viewModel is null)
        {
            return;
        }

        foreach (var route in _viewModel.GetRenderableRoutes())
        {
            if (!TryGetPortAnchorPoint(route.SourceEndpointId, ConnectorSide.Left, out var start) ||
                !TryGetPortAnchorPoint(route.TargetEndpointId, ConnectorSide.Right, out var end))
            {
                continue;
            }
            var isSelected = _viewModel.SelectedRouteId.HasValue && _viewModel.SelectedRouteId.Value == route.Id;

            var color = isSelected
                ? Color.FromRgb(0xF5, 0x7C, 0x00)
                : route.Enabled
                    ? Color.FromRgb(0x2E, 0x7D, 0x32)
                    : Color.FromRgb(0x75, 0x75, 0x75);

            var strokeBrush = new SolidColorBrush(color);
            var (geometry, controlPoint2) = BuildBezier(start, end);

            var path = new Path
            {
                Data = geometry,
                Stroke = strokeBrush,
                StrokeThickness = isSelected ? 7 : 5.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Cursor = Cursors.Hand,
                Tag = route.Id,
                ToolTip = $"{route.SourceName} -> {route.TargetName}\nStatus: {(route.Enabled ? "Aktiv" : "Inaktiv")}\nKanaele: {route.Channels}\nTypen: {route.MessageTypes}"
            };

            path.MouseLeftButtonDown += OnRoutePathMouseLeftButtonDown;
            RoutingCanvas.Children.Add(path);
            RoutingCanvas.Children.Add(CreateArrow(end, controlPoint2, strokeBrush, isSelected));
        }

        if (_dragState is not null)
        {
            DrawDragPreview(_dragState);
        }
    }

    private void DrawDragPreview(DragState dragState)
    {
        var start = TryGetPortAnchorPoint(dragState.EndpointId, dragState.Side, out var stableStart)
            ? stableStart
            : dragState.OriginPoint;

        var end = dragState.CurrentPoint;
        var isValidTarget = dragState.Target is not null;
        var previewColor = isValidTarget
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
            : new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5));

        var (geometry, controlPoint2) = BuildBezier(start, end);
        var previewPath = new Path
        {
            Data = geometry,
            Stroke = previewColor,
            StrokeThickness = 4.5,
            StrokeDashArray = new DoubleCollection([5, 4]),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };

        DragPreviewCanvas.Children.Add(previewPath);
        DragPreviewCanvas.Children.Add(CreateArrow(end, controlPoint2, previewColor, isSelected: false, isHitTestVisible: false));

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 33, 33, 33)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 11,
                Text = "Ziehen..."
            },
            IsHitTestVisible = false
        };

        Canvas.SetLeft(badge, end.X + 10);
        Canvas.SetTop(badge, end.Y - 24);
        DragPreviewCanvas.Children.Add(badge);
    }

    private static (PathGeometry Geometry, Point ControlPoint2) BuildBezier(Point start, Point end)
    {
        var distance = Math.Abs(end.X - start.X);
        var offset = Math.Clamp(distance * 0.35, 55, 220);

        var controlPoint1 = new Point(start.X + offset, start.Y);
        var controlPoint2 = new Point(end.X - offset, end.Y);

        var segment = new BezierSegment(controlPoint1, controlPoint2, end, true);
        var figure = new PathFigure(start, [segment], false);
        return (new PathGeometry([figure]), controlPoint2);
    }

    private static Polygon CreateArrow(
        Point end,
        Point controlPoint2,
        Brush brush,
        bool isSelected,
        bool isHitTestVisible = true)
    {
        var direction = end - controlPoint2;
        if (direction.Length < 0.001)
        {
            direction = new Vector(1, 0);
        }

        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        var arrowLength = isSelected ? 18 : 16;
        var halfWidth = isSelected ? 8 : 7;

        var p1 = end;
        var p2 = end - (direction * arrowLength) + (normal * halfWidth);
        var p3 = end - (direction * arrowLength) - (normal * halfWidth);

        return new Polygon
        {
            Points = new PointCollection([p1, p2, p3]),
            Fill = brush,
            Stroke = brush,
            StrokeThickness = 1,
            IsHitTestVisible = isHitTestVisible
        };
    }

    private void OnRoutePathMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        if (_viewModel is null || sender is not Path { Tag: Guid routeId })
        {
            return;
        }

        _viewModel.SelectRouteFromCanvas(routeId);
        ScheduleRouteRender();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _viewModel is null)
        {
            return;
        }

        if (e.OriginalSource is TextBoxBase)
        {
            return;
        }

        if (_viewModel.DeleteSelectedRouteFromCanvas())
        {
            ScheduleRouteRender();
            e.Handled = true;
        }
    }

    private void OnDeletePortRouteClick(object sender, RoutedEventArgs e)
    {
        Focus();
        if (_viewModel is null ||
            sender is not Button button ||
            button.DataContext is not RoutingViewModel.RouteEndpointRow row)
        {
            return;
        }

        _viewModel.SelectRoutePort(row.Id);
        _ = _viewModel.TryDeleteRouteForPort(row.Id);
        ScheduleRouteRender();
        e.Handled = true;
    }

    private void OnRouteDesignerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleRouteRender();
    }

    private bool TryGetConnectorButton(string endpointId, ConnectorSide side, out Button button)
    {
        var map = side == ConnectorSide.Left ? _leftConnectorButtons : _rightConnectorButtons;
        return map.TryGetValue(endpointId, out button!);
    }

    private bool TryResolveOriginElement(
        string endpointId,
        ConnectorSide side,
        FrameworkElement fallback,
        out FrameworkElement originElement)
    {
        if (TryGetConnectorButton(endpointId, side, out var connectorButton))
        {
            originElement = connectorButton;
            return true;
        }

        originElement = fallback;
        return true;
    }

    private static bool TryGetConnectorSide(object? tag, out ConnectorSide side)
    {
        if (tag is string value)
        {
            if (string.Equals(value, "left", StringComparison.OrdinalIgnoreCase))
            {
                side = ConnectorSide.Left;
                return true;
            }

            if (string.Equals(value, "right", StringComparison.OrdinalIgnoreCase))
            {
                side = ConnectorSide.Right;
                return true;
            }
        }

        side = default;
        return false;
    }

    private Point GetConnectorEdgePoint(Button button, ConnectorSide side)
    {
        var localPoint = side == ConnectorSide.Left
            ? new Point(button.ActualWidth, button.ActualHeight / 2)
            : new Point(0, button.ActualHeight / 2);

        return button.TranslatePoint(localPoint, RoutingCanvas);
    }

    private Point GetElementEdgePoint(FrameworkElement element, ConnectorSide side)
    {
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        var localPoint = side == ConnectorSide.Left
            ? new Point(width, height / 2)
            : new Point(0, height / 2);

        return element.TranslatePoint(localPoint, RoutingCanvas);
    }

    private Point GetEndpointEdgePoint(string endpointId, ConnectorSide side, FrameworkElement fallback)
    {
        if (TryGetConnectorButton(endpointId, side, out var connectorButton))
        {
            return GetConnectorEdgePoint(connectorButton, side);
        }

        return GetElementEdgePoint(fallback, side);
    }

    private bool TryGetPortAnchorPoint(string endpointId, ConnectorSide side, out Point point)
    {
        point = default;
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return false;
        }

        if (TryGetConnectorButton(endpointId, side, out var connectorButton))
        {
            point = GetConnectorEdgePoint(connectorButton, side);
            return true;
        }

        if (TryGetEndpointContainer(endpointId, side, out var endpointContainer))
        {
            point = GetElementEdgePoint(endpointContainer, side);
            return true;
        }

        return TryEstimateOffscreenAnchorPoint(endpointId, side, out point);
    }

    private bool TryGetEndpointContainer(string endpointId, ConnectorSide side, out ListBoxItem container)
    {
        container = default!;
        if (_viewModel is null)
        {
            return false;
        }

        var routeList = side == ConnectorSide.Left ? _viewModel.LeftRoutePorts : _viewModel.RightRoutePorts;
        if (routeList.Count == 0)
        {
            return false;
        }

        var row = routeList.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, endpointId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return false;
        }

        var uiList = side == ConnectorSide.Left ? LeftPortsListBox : RightPortsListBox;
        var resolvedContainer = uiList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
        if (resolvedContainer is null)
        {
            return false;
        }

        container = resolvedContainer;
        return true;
    }

    private bool TryEstimateOffscreenAnchorPoint(string endpointId, ConnectorSide side, out Point point)
    {
        point = default;
        if (_viewModel is null)
        {
            return false;
        }

        var routeList = side == ConnectorSide.Left ? _viewModel.LeftRoutePorts : _viewModel.RightRoutePorts;
        if (routeList.Count == 0)
        {
            return false;
        }

        var index = -1;
        for (var i = 0; i < routeList.Count; i++)
        {
            if (string.Equals(routeList[i].Id, endpointId, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        var listBox = side == ConnectorSide.Left ? LeftPortsListBox : RightPortsListBox;
        if (listBox.ActualWidth <= 0 || listBox.ActualHeight <= 0)
        {
            return false;
        }

        var scrollViewer = side == ConnectorSide.Left ? _leftScrollViewer : _rightScrollViewer;
        var viewportHost = scrollViewer as FrameworkElement ?? listBox;
        var viewportTopLeft = viewportHost.TranslatePoint(new Point(0, 0), RoutingCanvas);
        var scrollOffset = scrollViewer?.VerticalOffset ?? 0d;
        var itemHeight = GetEstimatedRouteItemHeight(listBox);

        var y = viewportTopLeft.Y + (index * itemHeight) + (itemHeight / 2d) - scrollOffset;
        var x = side == ConnectorSide.Left
            ? viewportTopLeft.X + viewportHost.ActualWidth - 16d
            : viewportTopLeft.X + 16d;

        point = new Point(x, y);
        return true;
    }

    private static double GetEstimatedRouteItemHeight(ListBox listBox)
    {
        var itemCount = listBox.Items.Count;
        for (var i = 0; i < itemCount; i++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem container &&
                container.ActualHeight > 0.1)
            {
                return container.ActualHeight;
            }
        }

        return 52d;
    }

    private DependencyObject? GetHitVisual(Point pointOnControl)
    {
        var direct = InputHitTest(pointOnControl) as DependencyObject;
        if (direct is not null)
        {
            return direct;
        }

        var pointInDesigner = TranslatePoint(pointOnControl, RouteDesignerGrid);
        var hit = VisualTreeHelper.HitTest(RouteDesignerGrid, pointInDesigner);
        return hit?.VisualHit;
    }

    private bool TryResolveFallbackTarget(Point pointerOnControl, DragState dragState, out ConnectorHit target)
    {
        target = default!;

        var targetList = dragState.Side == ConnectorSide.Left ? RightPortsListBox : LeftPortsListBox;
        var targetSide = dragState.Side == ConnectorSide.Left ? ConnectorSide.Right : ConnectorSide.Left;

        var pointOnList = TranslatePoint(pointerOnControl, targetList);
        if (pointOnList.X < 0 || pointOnList.Y < 0 || pointOnList.X > targetList.ActualWidth || pointOnList.Y > targetList.ActualHeight)
        {
            return false;
        }

        ListBoxItem? bestItem = null;
        RoutingViewModel.RouteEndpointRow? bestRow = null;
        var bestDistance = double.MaxValue;

        foreach (var item in targetList.Items)
        {
            if (targetList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container ||
                item is not RoutingViewModel.RouteEndpointRow row)
            {
                continue;
            }

            var center = container.TranslatePoint(new Point(container.ActualWidth / 2, container.ActualHeight / 2), this);
            var distance = Math.Abs(center.Y - pointerOnControl.Y);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestItem = container;
            bestRow = row;
        }

        if (bestItem is null || bestRow is null)
        {
            return false;
        }

        _ = TryGetConnectorButton(bestRow.Id, targetSide, out var mappedButton);
        target = new ConnectorHit(bestRow.Id, targetSide, bestItem, mappedButton);
        return true;
    }

    private ConnectorHit? ResolveConnectorFromVisual(DependencyObject? visual)
    {
        var current = visual;
        while (current is not null)
        {
            if (current is Button button &&
                button.DataContext is RoutingViewModel.RouteEndpointRow row &&
                TryGetConnectorSide(button.Tag, out var side))
            {
                return new ConnectorHit(row.Id, side, button, button);
            }

            if (current is ListBoxItem listBoxItem &&
                listBoxItem.DataContext is RoutingViewModel.RouteEndpointRow itemRow &&
                TryResolveSideForListItem(listBoxItem, out var itemSide))
            {
                _ = TryGetConnectorButton(itemRow.Id, itemSide, out var mappedConnector);
                return new ConnectorHit(itemRow.Id, itemSide, listBoxItem, mappedConnector);
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private bool TryResolveSideForListItem(ListBoxItem listBoxItem, out ConnectorSide side)
    {
        if (IsDescendantOf(listBoxItem, LeftPortsListBox))
        {
            side = ConnectorSide.Left;
            return true;
        }

        if (IsDescendantOf(listBoxItem, RightPortsListBox))
        {
            side = ConnectorSide.Right;
            return true;
        }

        side = default;
        return false;
    }

    private static bool IsDescendantOf(DependencyObject descendant, DependencyObject ancestor)
    {
        var current = descendant;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        var current = element;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is T typed)
            {
                return typed;
            }

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count; index++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }

        return null;
    }

    private enum ConnectorSide
    {
        Left,
        Right
    }

    private sealed record DragCandidate(
        string EndpointId,
        ConnectorSide Side,
        FrameworkElement OriginElement,
        Point StartPoint);

    private sealed class DragState
    {
        public DragState(string endpointId, ConnectorSide side, FrameworkElement originElement, Point originPoint)
        {
            EndpointId = endpointId;
            Side = side;
            OriginElement = originElement;
            OriginPoint = originPoint;
        }

        public string EndpointId { get; }

        public ConnectorSide Side { get; }

        public FrameworkElement OriginElement { get; }

        public Button? OriginButton => OriginElement as Button;

        public Point OriginPoint { get; }

        public Point CurrentPoint { get; set; }

        public ConnectorHit? Target { get; set; }
    }

    private sealed record ConnectorHit(string EndpointId, ConnectorSide Side, FrameworkElement Element, Button? Button);

    private readonly record struct RouteEndpoints(string SourceEndpointId, string TargetEndpointId);
}
