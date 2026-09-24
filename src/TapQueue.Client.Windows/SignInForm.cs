namespace TapQueue.Client.Windows;

/// <summary>"Sign in as…": a domain account's name and password, for servers that ask for one.</summary>
public sealed class SignInForm : Form
{
    private readonly TextBox _username = new() { Width = 280 };
    private readonly TextBox _password = new() { Width = 280, UseSystemPasswordChar = true };
    private readonly Button _signIn = new() { Text = "Sign in", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(300, 0) };

    /// <param name="signIn">Signs in; returns why it didn't work, or null if it did.</param>
    public SignInForm(Func<string, string, Task<string?>> signIn, string? username)
    {
        Text = "Sign in to TapQueue";
        Icon = TrayIcon.Create(connected: true);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AcceptButton = _signIn;
        CancelButton = _cancel;
        _username.Text = username ?? "";

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(12), WrapContents = false };
        layout.Controls.AddRange([
            new Label { Text = "Sign in with your domain account to print.", AutoSize = true, Padding = new Padding(0, 0, 0, 8) },
            new Label { Text = "Username", AutoSize = true }, _username,
            new Label { Text = "Password", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _password,
            _error,
        ]);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([_cancel, _signIn]);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        Shown += (_, _) => (_username.Text.Length == 0 ? _username : _password).Focus();
        _signIn.Click += async (_, _) =>
        {
            if (_username.Text.Trim().Length == 0 || _password.Text.Length == 0)
            {
                _error.Text = "Enter your username and password.";
                return;
            }
            _signIn.Enabled = _username.Enabled = _password.Enabled = false;
            _error.Text = "Signing in…";
            _error.ForeColor = SystemColors.GrayText;
            var error = await signIn(_username.Text, _password.Text);
            if (error is null)
            {
                Close();
                return;
            }
            _error.ForeColor = Color.Firebrick;
            _error.Text = error;
            _signIn.Enabled = _username.Enabled = _password.Enabled = true;
            _password.SelectAll();
            _password.Focus();
        };
    }
}
