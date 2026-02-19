using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MidiRouter.UI.ViewModels;

namespace MidiRouter.UI.Views;

public partial class RoutingView : UserControl
{
    private RoutingViewModel? _viewModel;

    public RoutingView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachToViewModel(DataContext as RoutingViewModel);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachToViewModel(e.NewValue as RoutingViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.EditPortFocusRequested -= OnEditPortFocusRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = null;
    }

    private void AttachToViewModel(RoutingViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.EditPortFocusRequested -= OnEditPortFocusRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.EditPortFocusRequested += OnEditPortFocusRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnEditPortFocusRequested(object? sender, EventArgs e)
    {
        FocusEditPortTextBox();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(RoutingViewModel.SelectedPort), StringComparison.Ordinal))
        {
            return;
        }

        // Keep current behavior: selecting a port should make inline rename immediately reachable.
        FocusEditPortTextBox();
    }

    private void FocusEditPortTextBox()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            EditPortNameTextBox.Focus();
            Keyboard.Focus(EditPortNameTextBox);
            EditPortNameTextBox.SelectAll();
        }, DispatcherPriority.ContextIdle);
    }
}
