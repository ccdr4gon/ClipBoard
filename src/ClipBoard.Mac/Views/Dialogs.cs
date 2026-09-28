using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClipBoard.Views;

internal static class Dialogs
{
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
        return await dialog.ShowDialog<string[]?>(owner);
    }
    public static async Task<bool> Confirm(Window owner, string title, string message)
        => await Form(owner, title, message) != null;
}
