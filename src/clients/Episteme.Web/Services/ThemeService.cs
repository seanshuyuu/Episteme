using Microsoft.JSInterop;

namespace Episteme.Web.Services;

public sealed class ThemeService(IJSRuntime javascript)
{
    public ValueTask<ThemePreferences?> LoadAsync(CancellationToken cancellationToken = default) =>
        javascript.InvokeAsync<ThemePreferences?>("episteme.loadTheme", cancellationToken);

    public ValueTask SaveAsync(ThemePreferences preferences, CancellationToken cancellationToken = default) =>
        javascript.InvokeVoidAsync("episteme.saveTheme", cancellationToken, preferences);
}

public sealed record ThemePreferences(string Mode, string Density, string Accent)
{
    public static ThemePreferences Default { get; } = new("light", "comfortable", "#635bdb");
}
