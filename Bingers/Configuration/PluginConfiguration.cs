#pragma warning disable CA1819

using System;
using System.Collections.Generic;
using System.Linq;
using Bingers.Model;
using MediaBrowser.Model.Plugins;

namespace Bingers.Configuration;

/// <summary>
/// Plugin configuration class for the bingers.app plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        BingersUsers = Array.Empty<BingersUser>();
    }

    /// <summary>
    /// Gets or sets the bingers.app users.
    /// </summary>
    public BingersUser[] BingersUsers { get; set; }

    /// <summary>
    /// Adds a user to the bingers.app users.
    /// </summary>
    /// <param name="userGuid">The user Guid.</param>
    public void AddUser(Guid userGuid)
    {
        var bingersUsers = BingersUsers.ToList();
        bingersUsers.Add(new BingersUser
        {
            LinkedMbUserId = userGuid
        });
        BingersUsers = bingersUsers.ToArray();
    }

    /// <summary>
    /// Removes a user from the bingers.app users.
    /// </summary>
    /// <param name="userGuid">The user id.</param>
    public void RemoveUser(Guid userGuid)
    {
        var bingersUsers = BingersUsers.ToList();
        bingersUsers.RemoveAll(user => user.LinkedMbUserId == userGuid);
        BingersUsers = bingersUsers.ToArray();
    }

    /// <summary>
    /// Gets a list of all bingers.app users.
    /// </summary>
    /// <returns>IReadonlyList{BingersUser} with all bingers.app users.</returns>
    public IReadOnlyList<BingersUser> GetAllBingersUsers()
    {
        return BingersUsers.ToList();
    }
}
