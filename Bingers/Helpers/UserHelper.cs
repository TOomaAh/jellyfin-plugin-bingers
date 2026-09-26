using System;
using System.Collections.Generic;
using System.Linq;
using Bingers.Model;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Bingers.Helpers;

internal static class UserHelper
{
    public static BingersUser GetBingersUser(User user, bool linked = false)
    {
        return GetBingersUser(user.Id, linked);
    }

    public static BingersUser GetBingersUser(Guid userGuid, bool linked = false)
    {
        return Plugin.Instance.PluginConfiguration.GetAllBingersUsers().FirstOrDefault(user =>
            user.LinkedMbUserId != Guid.Empty
            && user.LinkedMbUserId.Equals(userGuid)
            && (!linked || user.IsLinked()));
    }

    /// <summary>
    /// Gets the users a scheduled task should process, from the stored Bingers configurations.
    /// </summary>
    /// <param name="userManager">The <see cref="IUserManager"/>.</param>
    /// <param name="logger">The task logger.</param>
    /// <param name="isEnabled">Whether the task's option is enabled for a user.</param>
    /// <param name="disabledMessage">Logged when the option is disabled, with the user name as only argument.</param>
    /// <returns>The linked users with the option enabled, with their Jellyfin user.</returns>
    public static List<(User User, BingersUser BingersUser)> GetTaskUsers(
        IUserManager userManager,
        ILogger logger,
        Func<BingersUser, bool> isEnabled,
        string disabledMessage)
    {
        var configured = Plugin.Instance.PluginConfiguration.GetAllBingersUsers()
            .Where(u => u.LinkedMbUserId != Guid.Empty)
            .ToList();

        var users = new List<(User User, BingersUser BingersUser)>();
        foreach (var bingersUser in configured)
        {
            var user = userManager.GetUserById(bingersUser.LinkedMbUserId);
            if (user == null)
            {
                logger.LogWarning(
                    "Ignoring the Bingers configuration of deleted Jellyfin user {UserId} (Bingers account {Account})",
                    bingersUser.LinkedMbUserId,
                    bingersUser.Username ?? bingersUser.Email ?? "none");
            }
            else if (!bingersUser.IsLinked())
            {
                logger.LogInformation(
                    "Jellyfin user {User} is not linked to a Bingers account{Reauth}",
                    user.Username,
                    bingersUser.NeedsReauthorization ? " (session expired, link it again)" : string.Empty);
            }
            else if (!isEnabled(bingersUser))
            {
#pragma warning disable CA2254 // Template should be a static expression: callers pass constant templates.
                logger.LogInformation(disabledMessage, user.Username);
#pragma warning restore CA2254
            }
            else
            {
                users.Add((user, bingersUser));
            }
        }

        logger.LogInformation("Processing {Count} of {Total} Bingers configurations", users.Count, configured.Count);
        return users;
    }

    /// <summary>
    /// Checks whether it's possible/allowed to sync a <see cref="BaseItem"/> for a <see cref="BingersUser"/>.
    /// </summary>
    /// <param name="item">Item to check.</param>
    /// <param name="bingersUser">The bingers.app user to check for.</param>
    /// <returns><see cref="bool"/> indicating if it's possible/allowed to sync this item.</returns>
    public static bool CanSync(BaseItem item, BingersUser bingersUser) => GetSyncBlockReason(item, bingersUser) == null;

    /// <summary>
    /// Explains why a <see cref="BaseItem"/> can't be synced for a <see cref="BingersUser"/>.
    /// </summary>
    /// <param name="item">Item to check.</param>
    /// <param name="bingersUser">The bingers.app user to check for.</param>
    /// <returns>The reason, or <c>null</c> if the item can be synced.</returns>
    public static string GetSyncBlockReason(BaseItem item, BingersUser bingersUser)
    {
        if (item.Path == null || item.LocationType == LocationType.Virtual)
        {
            return "virtual item without file";
        }

        var excluded = bingersUser.LocationsExcluded?.FirstOrDefault(directory => item.Path.Contains(directory, StringComparison.OrdinalIgnoreCase));
        if (excluded != null)
        {
            return $"in excluded folder \"{excluded}\"";
        }

        switch (item)
        {
            case Movie:
                return null;
            case Episode episode:
                if (episode.Series == null)
                {
                    return "episode without series";
                }

                if (episode.IsMissingEpisode)
                {
                    return "missing episode";
                }

                if (!episode.IndexNumber.HasValue)
                {
                    return "episode without episode number";
                }

                if (!(episode.ParentIndexNumber ?? episode.Season?.IndexNumber).HasValue)
                {
                    return "episode without season number";
                }

                return null;
            default:
                return $"unsupported item type {item.GetType().Name}";
        }
    }
}
