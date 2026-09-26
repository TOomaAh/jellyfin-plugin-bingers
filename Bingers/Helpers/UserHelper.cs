using System;
using System.Linq;
using Bingers.Model;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

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
