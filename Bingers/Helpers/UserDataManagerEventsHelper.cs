using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bingers.Api;
using Bingers.Model;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Bingers.Helpers;

/// <summary>
/// Helper class used to post items manually marked as played to bingers.app.
/// Events are debounced so that marking a whole season or series as played is sent in one go.
/// </summary>
internal sealed class UserDataManagerEventsHelper : IDisposable
{
    private readonly ILogger<UserDataManagerEventsHelper> _logger;
    private readonly BingersApi _bingersApi;
    private readonly Dictionary<Guid, List<BaseItem>> _queue;
    private readonly Timer _queueTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDataManagerEventsHelper"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger{UserDataManagerEventsHelper}"/>.</param>
    /// <param name="bingersApi">The <see cref="BingersApi"/>.</param>
    public UserDataManagerEventsHelper(ILogger<UserDataManagerEventsHelper> logger, BingersApi bingersApi)
    {
        _queue = new Dictionary<Guid, List<BaseItem>>();
        _logger = logger;
        _bingersApi = bingersApi;
        _queueTimer = new Timer(OnTimerCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Queues an item marked as played by a bingers.app user.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="bingersUser">The <see cref="BingersUser"/>.</param>
    public void QueueWatched(BaseItem item, BingersUser bingersUser)
    {
        lock (_queue)
        {
            if (!_queue.TryGetValue(bingersUser.LinkedMbUserId, out var items))
            {
                items = new List<BaseItem>();
                _queue[bingersUser.LinkedMbUserId] = items;
            }

            if (items.All(i => i.Id != item.Id))
            {
                items.Add(item);
            }

            _queueTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }
    }

    private async void OnTimerCallback(object state)
    {
        Dictionary<Guid, List<BaseItem>> queue;
        lock (_queue)
        {
            if (_queue.Count == 0)
            {
                return;
            }

            queue = new Dictionary<Guid, List<BaseItem>>(_queue);
            _queue.Clear();
        }

        foreach (var (userId, items) in queue)
        {
            var bingersUser = UserHelper.GetBingersUser(userId, true);
            if (bingersUser == null)
            {
                _logger.LogWarning("Dropping {Count} items marked played: Jellyfin user {UserId} is no longer linked to Bingers", items.Count, userId);
                continue;
            }

            _logger.LogVerbose(bingersUser.ExtraLogging, "Sending {Count} items manually marked played to Bingers for user {UserId}", items.Count, userId);

            foreach (var item in items)
            {
                try
                {
                    await _bingersApi.MarkWatchedAsync(bingersUser, item, false, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (BingersApiException ex) when (ex.IsAuthError)
                {
                    _logger.LogWarning("Bingers session of user {UserId} is no longer valid; link the account again", userId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to mark {Item} as watched on Bingers", item.Name);
                }
            }
        }
    }

    public void Dispose()
    {
        _queueTimer.Dispose();
    }
}
