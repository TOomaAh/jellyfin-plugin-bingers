namespace Bingers.Api;

/// <summary>
/// Reference to a watchable bingers.app entity (a movie or an episode).
/// </summary>
/// <param name="EntityKind">The entity kind: <c>movie</c> or <c>episode</c>.</param>
/// <param name="EntityId">The entity id.</param>
/// <param name="TitleId">The catalog title id (movie or show).</param>
public record BingersEntityRef(string EntityKind, string EntityId, string TitleId)
{
    /// <summary>
    /// Movie entity kind.
    /// </summary>
    public const string Movie = "movie";

    /// <summary>
    /// Episode entity kind.
    /// </summary>
    public const string Episode = "episode";
}
