using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Pathman.Gui.Controls;

public sealed class InlineEditEventArgs(string value, bool wasChanged) : EventArgs
{
    public string Value { get; } = value;
    public bool WasChanged { get; } = wasChanged;
    public Task<string?> Completion { get; set; } = Task.FromResult<string?>(null);
}

public partial class EditableRow : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(EditableRow), new PropertyMetadata(""));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(EditableRow), new PropertyMetadata("", UpdateDetail));
    public static readonly DependencyProperty CanDeleteProperty = DependencyProperty.Register(nameof(CanDelete), typeof(bool), typeof(EditableRow), new PropertyMetadata(true));
    public static readonly DependencyProperty ShowPinProperty = DependencyProperty.Register(nameof(ShowPin), typeof(bool), typeof(EditableRow), new PropertyMetadata(true, UpdatePin));
    public static readonly DependencyProperty IsPinnedProperty = DependencyProperty.Register(nameof(IsPinned), typeof(bool), typeof(EditableRow), new PropertyMetadata(false, UpdatePin));
    public static readonly DependencyProperty EditOnDoubleClickProperty = DependencyProperty.Register(nameof(EditOnDoubleClick), typeof(bool), typeof(EditableRow), new PropertyMetadata(true));
    public static readonly DependencyProperty ShowEditProperty = DependencyProperty.Register(nameof(ShowEdit), typeof(bool), typeof(EditableRow), new PropertyMetadata(true, UpdateActions));
    public static readonly DependencyProperty ShowDeleteProperty = DependencyProperty.Register(nameof(ShowDelete), typeof(bool), typeof(EditableRow), new PropertyMetadata(true, UpdateActions));
    public static readonly DependencyProperty ShowJumpProperty = DependencyProperty.Register(nameof(ShowJump), typeof(bool), typeof(EditableRow), new PropertyMetadata(false, UpdateActions));
    public static readonly DependencyProperty JumpToolTipProperty = DependencyProperty.Register(nameof(JumpToolTip), typeof(string), typeof(EditableRow), new PropertyMetadata("转到引用变量"));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public bool CanDelete { get => (bool)GetValue(CanDeleteProperty); set => SetValue(CanDeleteProperty, value); }
    public bool ShowPin { get => (bool)GetValue(ShowPinProperty); set => SetValue(ShowPinProperty, value); }
    public bool IsPinned { get => (bool)GetValue(IsPinnedProperty); set => SetValue(IsPinnedProperty, value); }
    public bool EditOnDoubleClick { get => (bool)GetValue(EditOnDoubleClickProperty); set => SetValue(EditOnDoubleClickProperty, value); }
    public bool ShowEdit { get => (bool)GetValue(ShowEditProperty); set => SetValue(ShowEditProperty, value); }
    public bool ShowDelete { get => (bool)GetValue(ShowDeleteProperty); set => SetValue(ShowDeleteProperty, value); }
    public bool ShowJump { get => (bool)GetValue(ShowJumpProperty); set => SetValue(ShowJumpProperty, value); }
    public string JumpToolTip { get => (string)GetValue(JumpToolTipProperty); set => SetValue(JumpToolTipProperty, value); }
    public bool IsEditing { get; private set; }
    private bool inputChanged;
    private Task<bool>? pending;
    public event EventHandler<InlineEditEventArgs>? EditRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? PinRequested;
    public event EventHandler? EditingChanged;
    public event EventHandler? Activated;
    public event EventHandler? EditingCanceled;
    public event EventHandler? JumpRequested;
    public EditableRow() { InitializeComponent(); Input.TextChanged += (_,_) => { if(IsEditing) inputChanged=true; }; Loaded += (_, _) => { RefreshPin(); RefreshDetail(); RefreshActions(); }; }
    private static void UpdateActions(DependencyObject target, DependencyPropertyChangedEventArgs e) => ((EditableRow)target).RefreshActions();
    private void RefreshActions()
    {
        if (EditButton is null) return;
        EditButton.Visibility = ShowEdit ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = ShowDelete ? Visibility.Visible : Visibility.Collapsed;
        JumpButton.Visibility = ShowJump ? Visibility.Visible : Visibility.Collapsed;
    }
    private static void UpdateDetail(DependencyObject target, DependencyPropertyChangedEventArgs e) => ((EditableRow)target).RefreshDetail();
    private void RefreshDetail()
    {
        if (NameColumn is null) return;
        NameColumn.Width = new GridLength(string.IsNullOrEmpty(Detail) ? 1 : 3, GridUnitType.Star);
        DetailColumn.Width = string.IsNullOrEmpty(Detail) ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
    }
    private static void UpdatePin(DependencyObject target, DependencyPropertyChangedEventArgs e) => ((EditableRow)target).RefreshPin();
    private void RefreshPin()
    {
        if (PinButton is null) return;
        PinButton.Visibility = ShowPin ? Visibility.Visible : Visibility.Collapsed;
        PinButton.Content = IsPinned ? "\uE77A" : "\uE718";
        PinButton.ToolTip = IsPinned ? "取消置顶" : "置顶";
        System.Windows.Automation.AutomationProperties.SetName(PinButton, IsPinned ? "取消置顶" : "置顶");
    }
    public void BeginEdit()
    {
        if (IsEditing || !IsEnabled) return;
        IsEditing = true; Input.Text = Text; inputChanged=false; Input.ClearValue(BorderBrushProperty); Input.ToolTip = null;
        Label.Visibility = Visibility.Collapsed; Input.Visibility = Visibility.Visible; Actions.Visibility = Visibility.Collapsed;
        EditingChanged?.Invoke(this, EventArgs.Empty);
        Input.Focus(); Input.SelectAll();
    }
    public Task<bool> CommitAsync()
    {
        if (pending is not null) return pending;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion.Task;
        _ = CompleteAsync(completion);
        return completion.Task;
    }
    private async Task CompleteAsync(TaskCompletionSource<bool> completion)
    {
        try { completion.TrySetResult(await CommitCoreAsync()); }
        catch(Exception error) { completion.TrySetException(error); }
        finally { pending=null; }
    }
    private async Task<bool> CommitCoreAsync()
    {
        if (!IsEditing) return true;
        Input.IsReadOnly = true;
        try
        {
            var args = new InlineEditEventArgs(Input.Text,inputChanged); EditRequested?.Invoke(this, args);
            var error = await args.Completion;
            if (error is not null)
            {
                Input.BorderBrush = Brushes.Firebrick; Input.ToolTip = error;
                Input.Focus(); Input.SelectAll(); return false;
            }
            EndEdit(); return true;
        }
        finally { Input.IsReadOnly = false; }
    }
    private void EndEdit()
    {
        IsEditing = false; Input.Visibility = Visibility.Collapsed; Label.Visibility = Visibility.Visible; Actions.Visibility = Visibility.Visible;
        EditingChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Edit(object sender, RoutedEventArgs e) { e.Handled = true; BeginEdit(); }
    private void Pin(object sender, RoutedEventArgs e) { e.Handled = true; PinRequested?.Invoke(this, EventArgs.Empty); }
    private void Delete(object sender, RoutedEventArgs e) { e.Handled = true; DeleteRequested?.Invoke(this, EventArgs.Empty); }
    private void Jump(object sender, RoutedEventArgs e) { e.Handled = true; JumpRequested?.Invoke(this, EventArgs.Empty); }
    private void DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) is not null || IsEditing) return;
        e.Handled = true;
        if (EditOnDoubleClick) BeginEdit(); else Activated?.Invoke(this, EventArgs.Empty);
    }
    private void RowKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.F2 && !IsEditing) { e.Handled = true; BeginEdit(); } }
    private async void InputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await CommitAsync(); }
        else if (e.Key == Key.Escape && pending is null) { e.Handled = true; EndEdit(); EditingCanceled?.Invoke(this, EventArgs.Empty); Focus(); }
    }
    private async void InputLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    { if (IsEditing && pending is null) await CommitAsync(); }
    internal static T? FindParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T found) return found;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
