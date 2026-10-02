using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// 社交通知（11 号 P2）：城镇在场状态翻转 → 好友上下线推送与初始列表。
/// 扇出按用户定案为「反向边现查」——角色 X 上/下线时查「把 X 加为好友的所有角色」
/// （friend_character_id = X），对其在场者推 NOTI 54/55；进入城镇即推 NOTI 56
/// 初始列表（在线态经中枢在场表解析）。
/// </summary>
public sealed class SocialPresenceNotifier(
    IGameStore store,
    TownPresenceService town,
    CharacterSessionRegistry characterSessions)
{
    /// <summary>挂接中枢翻转钩子（组合根调用一次）。</summary>
    public void Register() => town.RegisterTransitionHook(OnTransition);

    private void OnTransition(TownPresenceTransition transition)
    {
        try
        {
            var entry = transition.Entry;
            if (transition.Kind == TownPresenceTransitionKind.Entered)
            {
                // 初始好友列表 + 上线扇出。
                SendFriendList(entry.SessionId, entry.CharacterId);
                FanOut(entry, SocialPacketBuilders.CreateBuddyIn);
            }
            else
            {
                // 下线扇出（离开城镇即视为对好友下线；副本在场语义留真机观察）。
                FanOut(entry, SocialPacketBuilders.CreateBuddyOut);
            }
        }
        catch (Exception exception)
        {
            // 钩子异常不得影响在场状态（docs/design/10 §4.1 纪律）。
            RuntimeLogger.Warn(
                exception,
                "Social presence notification failed for session {SessionId}.",
                transition.Entry.SessionId);
        }
    }

    /// <summary>增删后的实时刷新（handler 成功路径调用）：重发 NOTI 56 全量列表。</summary>
    internal void SendFriendList(Guid sessionId, Guid characterId) =>
        SendFriendList(store, town, characterSessions, sessionId, characterId);

    public static void SendFriendList(
        IGameStore store,
        TownPresenceService town,
        CharacterSessionRegistry characterSessions,
        Guid sessionId,
        Guid characterId)
    {
        var friends = store.GetFriendsAsync(characterId).GetAwaiter().GetResult();
        var entries = new List<SocialPacketBuilders.FriendListEntry>(friends.Length);
        foreach (var friend in friends)
        {
            var presence = town.FindByCharacter(friend.CharacterId);
            entries.Add(new SocialPacketBuilders.FriendListEntry(
                presence?.UserId ?? 0,
                friend.Name,
                Online: presence is not null,
                friend.Level,
                friend.Job,
                friend.Grow,
                friend.AccountUid));
        }

        characterSessions.NotifyGamePacket(
            sessionId,
            SocialPacketBuilders.CreateBuddyConnList(entries));
    }

    private void FanOut(TownPresenceEntry entry, Func<string, GameServerPacket> build)
    {
        // 反向边现查：转换低频（登录/登出），一条索引查询 + 在场求交。
        var owners = store.GetFriendOwnerIdsAsync(entry.CharacterId).GetAwaiter().GetResult();
        foreach (var ownerCharacterId in owners)
        {
            var presence = town.FindByCharacter(ownerCharacterId);
            if (presence is not null)
            {
                characterSessions.NotifyGamePacket(presence.SessionId, build(entry.Name));
            }
        }
    }
}
