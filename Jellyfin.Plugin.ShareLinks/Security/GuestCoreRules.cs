using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ShareLinks.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.ShareLinks.Security;

/// <summary>What the guard does with a guest's call to Jellyfin's own API.</summary>
public enum GuestCoreDecision
{
    /// <summary>Let the call through.</summary>
    Allow,

    /// <summary>Refuse with 403.</summary>
    Deny,

    /// <summary>Answer 404, as if the thing asked for did not exist.</summary>
    NotFound,

    /// <summary>Take the guest's session out of its SyncPlay group instead.</summary>
    LeaveGroup,

    /// <summary>Allow only when every item the live stream request names is one the guest can open.</summary>
    CheckLiveStream
}

/// <summary>A guest's call to a Jellyfin core action, as far as the rules need to know it.</summary>
/// <param name="Controller">The controller name, such as <c>SyncPlay</c>.</param>
/// <param name="Action">The action method name, such as <c>SyncPlayJoinGroup</c>.</param>
/// <param name="Share">The guest's live share, or null when it has none any more.</param>
/// <param name="GroupId">The SyncPlay group the call names, when it names one.</param>
/// <param name="UserId">The user the call names in its route, when it names one.</param>
/// <param name="CallerId">The guest's own user id.</param>
public sealed record GuestCall(
    string Controller,
    string Action,
    ShareLinkRecord? Share = null,
    Guid? GroupId = null,
    Guid? UserId = null,
    Guid CallerId = default);

/// <summary>
/// The rules for guests on Jellyfin's own API, beyond the tag policy. Pure, so every
/// case can be tested without a server.
/// </summary>
/// <remarks>
/// The tag policy already bounds what a guest can see. These rules cover the places
/// it does not reach: SyncPlay groups, which are shared between users and keyed by id
/// rather than by item, the Live TV endpoints that list server-wide schedules, the
/// live stream endpoint that opens a tuner from a token instead of an item id, and
/// the session and user endpoints, where every viewer of a multi-use link is the same
/// Jellyfin user and would otherwise see and steer each other's sessions, and any
/// guest could list every account on the server. Anything not listed is decided as
/// before.
/// </remarks>
public static class GuestCoreRules
{
    /// <summary>SyncPlay calls a guest may always make while in a group: they report its own player's state.</summary>
    private static readonly HashSet<string> SyncPlayReporting = new(StringComparer.Ordinal)
    {
        "SyncPlayLeaveGroup",
        "SyncPlayPing",
        "SyncPlayBuffering",
        "SyncPlayReady",
        "SyncPlaySetIgnoreWait"
    };

    /// <summary>SyncPlay calls that change what the whole group sees or hears.</summary>
    private static readonly HashSet<string> SyncPlayControl = new(StringComparer.Ordinal)
    {
        "SyncPlaySetNewQueue",
        "SyncPlaySetPlaylistItem",
        "SyncPlayRemoveFromPlaylist",
        "SyncPlayMovePlaylistItem",
        "SyncPlayQueue",
        "SyncPlayUnpause",
        "SyncPlayPause",
        "SyncPlayStop",
        "SyncPlaySeek",
        "SyncPlayNextItem",
        "SyncPlayPreviousItem",
        "SyncPlaySetRepeatMode",
        "SyncPlaySetShuffleMode"
    };

    /// <summary>
    /// Live TV calls a guest may make: enough to open a channel's page and play it.
    /// Timers, recordings, tuners, listing providers and channel mappings describe the
    /// whole server and are refused, even to a guest who was sent a channel.
    /// </summary>
    private static readonly HashSet<string> LiveTvAllowed = new(StringComparer.Ordinal)
    {
        "GetLiveTvChannels",
        "GetChannel",
        "GetLiveTvPrograms",
        "GetPrograms",
        "GetRecommendedPrograms",
        "GetProgram",
        "GetLiveStreamFile"
    };

    /// <summary>
    /// Session calls that steer another session: what it shows, plays or says, and
    /// who is signed in to it. Viewers of a multi-use link share one account, and
    /// Jellyfin lets an account steer all of its own sessions.
    /// </summary>
    private static readonly HashSet<string> SessionControl = new(StringComparer.Ordinal)
    {
        "DisplayContent",
        "Play",
        "SendPlaystateCommand",
        "SendSystemCommand",
        "SendGeneralCommand",
        "SendFullGeneralCommand",
        "SendMessageCommand",
        "AddUserToSession",
        "RemoveUserFromSession"
    };

    /// <summary>Decides a guest's call to a Jellyfin core action.</summary>
    /// <param name="call">The call.</param>
    /// <param name="isGroupAllowed">Whether the share may see or join a given group.</param>
    public static GuestCoreDecision Evaluate(GuestCall call, Func<ShareLinkRecord, Guid, bool> isGroupAllowed)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(isGroupAllowed);

        switch (call.Controller)
        {
            case "SyncPlay":
                return EvaluateSyncPlay(call.Action, call.Share, call.GroupId, isGroupAllowed);

            case "LiveTv":
                return LiveTvAllowed.Contains(call.Action) ? GuestCoreDecision.Allow : GuestCoreDecision.Deny;

            case "MediaInfo" when string.Equals(call.Action, "OpenLiveStream", StringComparison.Ordinal):
                return GuestCoreDecision.CheckLiveStream;

            case "Session":
                // The session list itself is let through and cut down to the
                // caller's own session by the guard.
                return SessionControl.Contains(call.Action) ? GuestCoreDecision.Deny : GuestCoreDecision.Allow;

            case "User" when string.Equals(call.Action, "GetUserById", StringComparison.Ordinal):
                // Any signed-in account can read any other by id. A guest only
                // needs its own. The user list is cut down by the guard.
                return call.UserId.HasValue && call.UserId.Value.Equals(call.CallerId) && !call.CallerId.Equals(Guid.Empty)
                    ? GuestCoreDecision.Allow
                    : GuestCoreDecision.NotFound;
        }

        return GuestCoreDecision.Allow;
    }

    private static GuestCoreDecision EvaluateSyncPlay(
        string action,
        ShareLinkRecord? share,
        Guid? requestedGroupId,
        Func<ShareLinkRecord, Guid, bool> isGroupAllowed)
    {
        // Leaving is always fine, and is the one thing a guest whose share just
        // ended should still be able to do.
        if (string.Equals(action, "SyncPlayLeaveGroup", StringComparison.Ordinal))
        {
            return GuestCoreDecision.Allow;
        }

        if (share is null)
        {
            return GuestCoreDecision.Deny;
        }

        switch (action)
        {
            case "SyncPlayCreateGroup":
                // A party guest is there to join the host. Anyone else may start a
                // group; the guard remembers it so their fellow viewers can join.
                return share.WatchParty ? GuestCoreDecision.Deny : GuestCoreDecision.Allow;

            case "SyncPlayJoinGroup":
                return requestedGroupId.HasValue && isGroupAllowed(share, requestedGroupId.Value)
                    ? GuestCoreDecision.Allow
                    : GuestCoreDecision.Deny;

            case "SyncPlayGetGroup":
                return requestedGroupId.HasValue && isGroupAllowed(share, requestedGroupId.Value)
                    ? GuestCoreDecision.Allow
                    : GuestCoreDecision.NotFound;

            case "SyncPlayGetGroups":
                // Let through; the guard drops every group the share may not see
                // from the answer.
                return GuestCoreDecision.Allow;
        }

        if (SyncPlayReporting.Contains(action))
        {
            return GuestCoreDecision.Allow;
        }

        if (SyncPlayControl.Contains(action))
        {
            if (!share.WatchPartyHostOnly)
            {
                return GuestCoreDecision.Allow;
            }

            // Stop would end playback for the whole group. A guest pressing it
            // wants out, so take them out of the group and let them stop alone.
            return string.Equals(action, "SyncPlayStop", StringComparison.Ordinal)
                ? GuestCoreDecision.LeaveGroup
                : GuestCoreDecision.Deny;
        }

        // A SyncPlay action this build does not know about. It may change the group
        // for everyone, so it is refused until someone has looked at it.
        return GuestCoreDecision.Deny;
    }

    /// <summary>
    /// Gets a value indicating whether an item is Live TV the guest was not sent:
    /// any channel but the shared one, or a programme on any other channel.
    /// </summary>
    /// <remarks>
    /// Jellyfin skips the allowed-tags check for an item whose parent is a view or
    /// the root, and every channel sits directly under the Live TV view. So a
    /// tag-restricted account can open any channel by id, get its playback info and
    /// with it a token that opens the tuner, Live TV access or not. Channel ids are
    /// derived from the tuner's own channel ids, so they can be worked out. The tag
    /// policy cannot be relied on here; this check stands in for it.
    /// </remarks>
    public static bool IsOutsideLiveTvShare(BaseItem item, ShareLinkRecord? share)
    {
        ArgumentNullException.ThrowIfNull(item);

        Guid channelId;
        switch (item)
        {
            case LiveTvChannel channel:
                channelId = channel.Id;
                break;
            case LiveTvProgram program:
                channelId = program.ChannelId;
                break;
            default:
                return false;
        }

        return share is null
            || share.Kind != ShareKind.LiveTv
            || !Guid.TryParse(share.ItemId, out var sharedChannel)
            || !sharedChannel.Equals(channelId);
    }

    /// <summary>
    /// Pulls the item ids out of a live stream open token. Jellyfin's tokens are
    /// underscore-separated and carry the item id in its 32-digit form; the provider
    /// hash in front has the same shape, so callers have to check which ones are items.
    /// Every part is tried in every form Jellyfin itself would parse, so writing the
    /// id with dashes or braces does not slip it past the check.
    /// </summary>
    public static IReadOnlyList<Guid> ExtractTokenGuids(string? openToken)
    {
        var ids = new List<Guid>();
        if (string.IsNullOrWhiteSpace(openToken))
        {
            return ids;
        }

        foreach (var part in openToken.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(part, out var id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
