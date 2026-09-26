#pragma warning disable CA1819

using System;

namespace Bingers.Model;

/// <summary>
/// Bingers.app user class, linked to a Jellyfin user.
/// </summary>
public class BingersUser
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BingersUser"/> class.
    /// </summary>
    public BingersUser()
    {
        LinkedMbUserId = Guid.Empty;
        Cookies = Array.Empty<BingersCookie>();
        Scrobble = true;
        PostSetWatched = true;
        PostSetUnwatched = true;
        PostWatchedHistory = true;
        SkipWatchedImportFromBingers = false;
        CleanupWatchedHistory = false;
        CleanupDryRun = true;
        MarkMoviesAsRewatched = false;
        MarkEpisodesAsRewatched = false;
        ExtraLogging = false;
        LocationsExcluded = null;
    }

    /// <summary>
    /// Gets or sets the linked Jellyfin user id.
    /// </summary>
    public Guid LinkedMbUserId { get; set; }

    /// <summary>
    /// Gets or sets the bingers.app session cookies.
    /// </summary>
    public BingersCookie[] Cookies { get; set; }

    /// <summary>
    /// Gets or sets the session expiration date, if known.
    /// </summary>
    public DateTime? SessionExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the bingers.app user id.
    /// </summary>
    public string BingersUserId { get; set; }

    /// <summary>
    /// Gets or sets the bingers.app user name.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets the bingers.app email.
    /// </summary>
    public string Email { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the stored session was rejected by bingers.app and must be re-linked.
    /// </summary>
    public bool NeedsReauthorization { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether items played to completion should be marked watched on bingers.app.
    /// </summary>
    public bool Scrobble { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether manually marking an item as played should be posted to bingers.app.
    /// </summary>
    public bool PostSetWatched { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether manually marking an item as unplayed should mark it unwatched on bingers.app.
    /// </summary>
    public bool PostSetUnwatched { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the scheduled task should export the watched history to bingers.app.
    /// </summary>
    public bool PostWatchedHistory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the import task must leave the Jellyfin watched status untouched.
    /// </summary>
    public bool SkipWatchedImportFromBingers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the clean up task may process this user: entries watched on bingers.app
    /// whose library items are not played in Jellyfin are marked as not watched.
    /// </summary>
    public bool CleanupWatchedHistory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the clean up task only logs what it would change.
    /// </summary>
    public bool CleanupDryRun { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether watching an already watched movie again increments its play count on bingers.app.
    /// </summary>
    public bool MarkMoviesAsRewatched { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether watching an already watched episode again increments its play count on bingers.app.
    /// </summary>
    public bool MarkEpisodesAsRewatched { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether extra logging is enabled or not.
    /// </summary>
    public bool ExtraLogging { get; set; }

    /// <summary>
    /// Gets or sets the excluded library locations.
    /// </summary>
    public string[] LocationsExcluded { get; set; }

    /// <summary>
    /// Gets a value indicating whether this user has a stored bingers.app session.
    /// </summary>
    /// <returns><c>true</c> if the user has session cookies.</returns>
    public bool IsLinked() => Cookies != null && Cookies.Length > 0;
}
