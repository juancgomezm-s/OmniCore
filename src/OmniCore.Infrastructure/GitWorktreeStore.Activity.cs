using OmniCore.Abstractions;
using OmniCore.Domain;

namespace OmniCore.Infrastructure;

public sealed partial class GitWorktreeStore : IGitActivityReader
{
    public async Task<GitActivitySnapshot?> ReadActivityAsync(string directory, CancellationToken cancellationToken)
    {
        // Reuse the existing argv-only runtime and defensive Git configuration. This also
        // supports linked worktrees, packed objects, SHA-256 repositories and detached HEAD.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var root = Canonical(directory);
            if (HasReparseDirectoryComponent(root)) return null;
            var result = await Git(root, ["show", "--no-patch", "--no-show-signature", "--format=%H%n%T%n%P", "HEAD"], null, deadline.Token).ConfigureAwait(false);
            if (!result.Success) return null;
            var lines = result.Output.Replace("\r", "").TrimEnd('\n').Split('\n');
            if (lines.Length < 2 || !Hash(lines[0]) || !Hash(lines[1])) return null;
            var parents = lines.Length > 2 ? lines[2].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            return parents.All(Hash) ? new(lines[0], lines[1], parents) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or OperationCanceledException) { return null; }
        static bool Hash(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
    }
}
