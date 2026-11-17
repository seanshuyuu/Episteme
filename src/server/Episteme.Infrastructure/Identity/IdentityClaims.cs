using System.Security.Cryptography;
using System.Text;

namespace Episteme.Infrastructure.Identity;

public static class IdentityClaims
{
    public const string TokenGeneration = "episteme_token_generation";
    public const string SecurityStampHash = "episteme_security_stamp";

    public static string? HashSecurityStamp(string? securityStamp) =>
        string.IsNullOrEmpty(securityStamp) ? null :
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(securityStamp)));
}
