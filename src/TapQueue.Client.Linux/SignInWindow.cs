using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace TapQueue.Client.Linux;

/// <summary>"Sign in as…": a domain account's name and password, for servers that ask for one.</summary>
public sealed class SignInWindow : Window
{
    private readonly TextBox _username = new() { Width = 300 };
    private readonly TextBox _password = new() { Width = 300, PasswordChar = '•' };
    private readonly Button _signIn = new() { Content = "Sign in", IsDefault = true };
    private readonly Button _cancel = new() { Content = "Cancel", IsCancel = true };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };

    /// <param name="signIn">Signs in; returns why it didn't work, or null if it did.</param>
    public SignInWindow(TrayApp app, Func<string, string, Task<string?>> signIn, string? username)
    {
        Title = "Sign in to TapQueue";
        Icon = app.AppIcon;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _username.Text = username ?? "";

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { _cancel, _signIn } };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Sign in with your domain account to print.", Margin = new Thickness(0, 0, 0, 6) },
                new TextBlock { Text = "Username" }, _username,
                new TextBlock { Text = "Password" }, _password,
                _error,
                buttons,
            },
        };

        Opened += (_, _) => (string.IsNullOrEmpty(_username.Text) ? _username : _password).Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        _cancel.Click += (_, _) => Close();
        _signIn.Click += async (_, _) =>
        {
            var name = _username.Text?.Trim() ?? "";
            var password = _password.Text ?? "";
            if (name.Length == 0 || password.Length == 0)
            {
                ShowError("Enter your username and password.");
                return;
            }
            _signIn.IsEnabled = _username.IsEnabled = _password.IsEnabled = false;
            _error.Foreground = Brushes.Gray;
            _error.Text = "Signing in…";
            var error = await signIn(name, password);
            if (error is null)
            {
                Close();
                return;
            }
            _signIn.IsEnabled = _username.IsEnabled = _password.IsEnabled = true;
            ShowError(error);
            _password.SelectAll();
            _password.Focus();
        };
    }

    private void ShowError(string message)
    {
        _error.Foreground = Brushes.Firebrick;
        _error.Text = message;
    }
}
