using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClipBoard.Views;

internal static class Dialogs
{
    // All calls run on the UI thread. Keep the current dialog so a tray click/hotkey cannot raise its disabled owner.
    private static readonly Dictionary<Window, Window?> Active = new();
    public static bool HasModal(Window owner) => Active.ContainsKey(owner);
    public static bool ActivateModal(Window owner)
    {
        if (!Active.TryGetValue(owner, out var dialog)) return false;
        if (dialog?.IsVisible == true) dialog.Activate();
        return true;
    }

    public static Task ShowAsync(Window dialog, Window owner) => ShowAsync<object?>(dialog, owner);
    public static Task<T> ShowAsync<T>(Window dialog, Window owner)
        => RunModalAsync(owner, dialog, () => dialog.ShowDialog<T>(owner));

    public static Task<T> PickAsync<T>(Window owner, Func<Task<T>> picker)
        => RunModalAsync(owner, null, picker);

    private static async Task<T> RunModalAsync<T>(Window owner, Window? dialog, Func<Task<T>> show)
    {
        bool topmost = owner.Topmost;
        bool hadPrevious = Active.TryGetValue(owner, out var previous);
        Active[owner] = dialog;
        try
        {
            // A normal macOS dialog otherwise appears below a floating (Topmost) clipboard panel.
            owner.Topmost = false;
            if (dialog != null) dialog.Topmost = topmost;
            return await show();
        }
        finally
        {
            if (hadPrevious) Active[owner] = previous;
            else Active.Remove(owner);
            owner.Topmost = topmost;
            if (owner.IsVisible && !ActivateModal(owner)) owner.Activate();
        }
    }

    public static async Task<string[]?> Form(Window owner, string title, string description,
        params (string Label, string Value, bool Secret)[] fields)
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        var inputs = fields.Select(f => new TextBox { Text = f.Value, PasswordChar = f.Secret ? '●' : '\0' }).ToArray();
        for (int i = 0; i < fields.Length; i++)
        {
            panel.Children.Add(new TextBlock { Text = fields[i].Label });
            panel.Children.Add(inputs[i]);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消" }; var ok = new Button { Content = "确定" };
        buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons);
        var dialog = new Window { Title = title, Width = 500, MaxHeight = 750, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel }, CanResize = false };
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) => dialog.Close(inputs.Select(i => i.Text ?? "").ToArray());
        return await ShowAsync<string[]?>(dialog, owner);
    }
    public static async Task<bool> Confirm(Window owner, string title, string message)
        => await Form(owner, title, message) != null;
}
