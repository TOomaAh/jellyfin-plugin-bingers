using System;

namespace Bingers.Model;

/// <summary>
/// A cookie of the bingers.app session.
/// </summary>
public class BingersCookie
{
    /// <summary>
    /// Gets or sets the cookie name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Gets or sets the cookie value.
    /// </summary>
    public string Value { get; set; }

    /// <summary>
    /// Gets or sets the cookie expiration date (UTC), if any.
    /// </summary>
    public DateTime? Expires { get; set; }
}
