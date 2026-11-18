# Episteme Web

This Blazor Server app implements the ordinary-user and administrator prototype pages. Run it from the repository root with:

```powershell
dotnet run --project src/clients/Episteme.Web
```

Configure the OpenID Connect provider and API before starting the app:

| Environment variable | Purpose |
| --- | --- |
| `Authentication__Authority` | Identity provider base URL |
| `Authentication__ClientId` | Public OIDC client registered for this app |
| `Authentication__ApiAudience` | API audience/resource requested during login |
| `Api__BaseUrl` | Absolute API base URL; defaults to the audience |
| `Authentication__MetadataAddress` | Optional discovery document URL |
| `Authentication__TokenEndpoint` | Optional token endpoint used for refresh |

Register the app's `/signin-oidc` callback URL with the identity provider for the selected launch profile. The app uses authorization code flow with PKCE and does not require a client secret. Supply configuration through the environment or a local, untracked secrets provider; do not commit credentials.
