namespace Bingers.Api;

/// <summary>
/// Link status of a Jellyfin user.
/// </summary>
public class BingersLinkStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether a bingers.app session is stored.
    /// </summary>
    public bool IsLinked { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the session was revoked and the account must be linked again.
    /// </summary>
    public bool NeedsReauthorization { get; set; }

    /// <summary>
    /// Gets or sets the bingers.app user name.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets the bingers.app email.
    /// </summary>
    public string Email { get; set; }
}
