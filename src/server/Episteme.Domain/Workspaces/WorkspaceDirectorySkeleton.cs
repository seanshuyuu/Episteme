using System.Text;

namespace Episteme.Domain.Workspaces;

public static class WorkspaceDirectorySkeleton
{
    public const int MaximumDirectories = 256;
    public const int MaximumPathBytes = 65536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryNormalize(IReadOnlyList<string>? directories, out string[] normalized)
    {
        normalized = [];
        if (directories is null || directories.Count > MaximumDirectories)
            return false;
        var result = new HashSet<string>(StringComparer.Ordinal);
        var totalBytes = 0;
        try
        {
            foreach (var path in directories)
            {
                if (string.IsNullOrEmpty(path) || !UserWorkspaceSettings.IsValidDirectory(path) ||
                    !UserWorkspaceSettings.IsValidDirectory(path + "/.gitkeep") ||
                    path.Split('/').Any(s => s.Equals(".gitkeep", StringComparison.OrdinalIgnoreCase)))
                    return false;
                var segments = path.Split('/');
                for (var i = 1; i <= segments.Length; i++)
                {
                    var directory = string.Join('/', segments.Take(i));
                    if (result.Add(directory))
                    {
                        totalBytes += StrictUtf8.GetByteCount(directory);
                        if (result.Count > MaximumDirectories || totalBytes > MaximumPathBytes)
                            return false;
                    }
                }
            }
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        normalized = result.Order(StringComparer.Ordinal).ToArray();
        return true;
    }
}
