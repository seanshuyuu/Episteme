using System.Net.Mail;

namespace Episteme.Auth;

public sealed record IdentityMailOptions(bool Enabled, string Host, int Port, string From,
    string? UserName, string? Password)
{
    public static IdentityMailOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication:Email");
        var enabled = section.GetValue<bool>("Enabled");
        if (!enabled)
            return new(false, "", 0, "", null, null);

        var host = section["Host"];
        var port = section.GetValue<int?>("Port") ?? 587;
        var from = section["From"];
        var userName = section["UserName"];
        var password = section["Password"];
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            port is < 1 or > 65535 || !MailAddress.TryCreate(from, out var address) ||
            address.Address != from || string.IsNullOrWhiteSpace(from) ||
            (string.IsNullOrWhiteSpace(userName) != string.IsNullOrEmpty(password)))
            throw new InvalidOperationException("Invalid Authentication:Email configuration. Configure a host, port, sender and either both SMTP credentials or neither.");
        return new(true, host, port, from, string.IsNullOrWhiteSpace(userName) ? null : userName,
            string.IsNullOrEmpty(password) ? null : password);
    }

    public override string ToString() => "IdentityMailOptions { credentials redacted }";
}
