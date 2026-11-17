using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity.UI.Services;

namespace Episteme.Auth;

public sealed class SmtpIdentityEmailSender(IdentityMailOptions options) : IEmailSender
{
    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(options.From),
            Subject = subject,
            Body = IdentityEmailTemplate.Render(subject, htmlMessage),
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };
        message.To.Add(new MailAddress(email));
        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = true,
            Timeout = 15000,
            UseDefaultCredentials = false
        };
        if (options.UserName is not null)
            client.Credentials = new NetworkCredential(options.UserName, options.Password);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await client.SendMailAsync(message, timeout.Token);
        }
        catch (Exception exception) when (exception is SmtpException or IOException or OperationCanceledException)
        {
            throw new InvalidOperationException("Unable to send the identity email through the configured SMTP server.");
        }
    }
}

public static class IdentityEmailTemplate
{
    public static string Render(string subject, string identityMessage)
    {
        var title = HtmlEncoder.Default.Encode(subject);
        var instruction = subject switch
        {
            "Confirm your email" => "Confirm your email address to activate your account.",
            "Reset your password" => "Use the link below to choose a new password.",
            _ => "Please follow the instructions below."
        };
        return $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>{title}</title></head>
            <body style="font-family: sans-serif; line-height: 1.5;">
            <h1>Episteme</h1>
            <h2>{title}</h2>
            <p>{instruction}</p>
            <p>{identityMessage}</p>
            <p>If you did not request this, you can ignore this email. Do not share the link.</p>
            </body></html>
            """;
    }
}
