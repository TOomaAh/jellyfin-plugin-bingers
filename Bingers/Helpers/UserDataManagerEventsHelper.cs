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
/// Helper class used to post items manually marked as played or unplayed to bingers.app.
/// Events are debounced so that marking a whole season or series is sent in one go, and only the last state of an
/// item is sent: marking an item played by mistake then unplayed within the delay sends nothing.
/// </summary>
internal sealed class UserDataManagerEventsHelper : IDisposable
{
    private static readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger<UserDataManagerEventsHelper> _logger;
    private readonly BingersApi _bingersApi;
    private readonly Dictionary<Guid, Dictionary<Guid, PendingChange>> _queue;
    private readonly Timer _queueTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDataManagerEventsHelper"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger{UserDataManagerEventsHelper}"/>.</param>
    /// <param name="bingersApi">The <see cref="BingersApi"/>.</param>
    public UserDataManagerEventsHelper(ILogger<UserDataManagerEventsHelper> logger, BingersApi bingersApi)
    {
        _queue = new Dictionary<Guid, Dictionary<Guid, PendingChange>>();
        _logger = logger;
        _bingersApi = bingersApi;
        _queueTimer = new Timer(OnTimerCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Queues the new played state of an item for a bingers.app user. A later change of the same item replaces it.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="played">Whether the item was marked played (<c>true</c>) or unplayed (<c>false</c>).</param>
    /// <param name="send">Whether this state must be sent; <c>false</c> only cancels a pending change of the item.</param>
    /// <param name="bingersUser">The <see cref="BingersUser"/>.</param>
    public void QueuePlayedState(BaseItem item, bool played, bool send, BingersUser bingersUser)
    {
        lock (_queue)
        {
            if (!_queue.TryGetValue(bingersUser.LinkedMbUserId, out var items))
            {
                items = new Dictionary<Guid, PendingChange>();
                _queue[bingersUser.LinkedMbUserId] = items;
            }

            if (items.TryGetValue(item.Id, out var previous) && previous.Played != played)
            {
                _logger.LogVerbose(
                    bingersUser.ExtraLogging,
                    "{Item} was marked {State} before its pending '{Previous}' change was sent to Bingers; the pending change is cancelled",
                    item.Name,
                    played ? "played" : "unplayed",
                    previous.Played ? "watched" : "unwatched");
            }

            if (send)
            {
                items[item.Id] = new PendingChange(item, played);
            }
            else
            {
                items.Remove(item.Id);
            }

            _queueTimer.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async void OnTimerCallback(object state)
    {
        Dictionary<Guid, List<PendingChange>> queue;
        lock (_queue)
        {
            queue = _queue
                .Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Values.ToList());
            _queue.Clear();
        }

        foreach (var (userId, changes) in queue)
        {
            var bingersUser = UserHelper.GetBingersUser(userId, true);
            if (bingersUser == null)
            {
                _logger.LogWarning("Dropping {Count} played state changes: Jellyfin user {UserId} is no longer linked to Bingers", changes.Count, userId);
                continue;
            }

            _logger.LogVerbose(
                bingersUser.ExtraLogging,
                "Sending {Watched} items marked played and {Unwatched} marked unplayed to Bingers for user {UserId}",
                changes.Count(c => c.Played),
                changes.Count(c => !c.Played),
                userId);

            foreach (var change in changes)
            {
                try
                {
                    if (change.Played)
                    {
                        await _bingersApi.MarkWatchedAsync(bingersUser, change.Item, false, null, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        await _bingersApi.MarkUnwatchedAsync(bingersUser, change.Item, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (BingersApiException ex) when (ex.IsAuthError)
                {
                    _logger.LogWarning("Bingers session of user {UserId} is no longer valid; link the account again", userId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to mark {Item} as {State} on Bingers", change.Item.Name, change.Played ? "watched" : "unwatched");
                }
            }
        }
    }

    public void Dispose()
    {
        _queueTimer.Dispose();
    }

    private sealed record PendingChange(BaseItem Item, bool Played);
}
