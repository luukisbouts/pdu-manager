using System.Windows;
using PduManager.Drivers;
using PduManager.Models;
using PduManager.Services;

namespace PduManager.Views;

/// <summary>Add a PDU, or (when <c>existing</c> is given) edit its name, address, port and login.</summary>
public partial class AddPduWindow : Window
{
    private readonly PduConfig? _existing;
    private readonly bool _canReadUser, _canReadPass;

    public PduConfig? Result { get; private set; }

    public AddPduWindow(PduConfig? existing = null)
    {
        InitializeComponent();
        _existing = existing;
        if (existing is null) return;

        Title = "Edit PDU";
        OkBtn.Content = "Save";
        NameBox.Text = existing.Name;
        HostBox.Text = existing.Host;
        PortBox.Text = existing.Port.ToString();
        _canReadUser = CredentialStore.TryUnprotect(existing.EncryptedUser, out var user);
        _canReadPass = CredentialStore.TryUnprotect(existing.EncryptedPassword, out _);
        UserBox.Text = user;
        InfoText.Text = _canReadUser && _canReadPass
            ? "Leave the password empty to keep the stored one."
            : "The stored login cannot be read on this computer/Windows user. Enter the username and password again.";
    }

    private bool TryGetInput(out string host, out int port)
    {
        host = HostBox.Text.Trim();
        port = 0;
        if (host.Length == 0 || !int.TryParse(PortBox.Text, out port) || port is < 1 or > 65535)
        {
            ResultText.Text = "Enter a host and a valid port.";
            return false;
        }
        return true;
    }

    /// <summary>Typed values win; empty fields fall back to the stored login when it is readable.</summary>
    private bool TryGetLogin(out string user, out string pass)
    {
        user = UserBox.Text;
        pass = PassBox.Password;
        if (_existing is not null)
        {
            if (user.Length == 0 && _canReadUser) CredentialStore.TryUnprotect(_existing.EncryptedUser, out user);
            if (pass.Length == 0 && _canReadPass) CredentialStore.TryUnprotect(_existing.EncryptedPassword, out pass);
        }
        if (user.Length == 0 || (pass.Length == 0 && (_existing is null || !_canReadPass)))
        {
            ResultText.Text = "Enter the username and password.";
            return false;
        }
        return true;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInput(out var host, out var port) || !TryGetLogin(out var user, out var pass)) return;
        TestBtn.IsEnabled = false;
        ResultText.Text = "Testing…";
        try
        {
            using var driver = new ArgusHttpDriver(host, port, user, pass);
            var snap = await driver.GetSnapshotAsync();
            ResultText.Text = $"OK — found {snap.Outlets.Count} outlets.";
        }
        catch (Exception ex) { ResultText.Text = "Failed: " + ex.Message; }
        finally { TestBtn.IsEnabled = true; }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInput(out var host, out var port) || !TryGetLogin(out var user, out var pass)) return;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { ResultText.Text = "Enter a name."; return; }
        Result = new PduConfig
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            Name = NameBox.Text.Trim(),
            Host = host,
            Port = port,
            EncryptedUser = CredentialStore.Protect(user),
            EncryptedPassword = CredentialStore.Protect(pass),
        };
        DialogResult = true;
    }
}
