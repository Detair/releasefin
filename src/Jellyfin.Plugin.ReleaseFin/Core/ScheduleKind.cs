namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>What kind of library item a schedule drips. Serialized by name in both the plugin
/// XML configuration and the JSON API. Pre-1.1 configs have no Kind element, so XmlSerializer
/// leaves the property at its initializer — Series, the original behavior.</summary>
public enum ScheduleKind
{
    /// <summary>Drip a TV series' episodes in aired order.</summary>
    Series = 0,

    /// <summary>Drip a movie collection's (BoxSet's) movies in premiere order.</summary>
    Collection = 1,
}
