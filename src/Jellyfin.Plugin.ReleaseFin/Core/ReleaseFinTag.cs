namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>Tag naming and idempotent tag-list math. The releasefin- prefix is the plugin's
/// ownership boundary: code must never create or remove tags outside it.</summary>
public static class ReleaseFinTag
{
    public const string Prefix = "releasefin-";

    public static string For(Guid scheduleId) => Prefix + scheduleId.ToString("N");

    public static bool IsReleaseFinTag(string tag) =>
        tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string[] Add(string[] tags, string tag) =>
        tags.Contains(tag, StringComparer.OrdinalIgnoreCase) ? tags : [.. tags, tag];

    public static string[] Remove(string[] tags, string tag) =>
        tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray();
}
