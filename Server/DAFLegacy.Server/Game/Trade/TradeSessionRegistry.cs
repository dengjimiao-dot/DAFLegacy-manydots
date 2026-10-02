namespace DAFLegacy.Server;

/// <summary>CMD 26 单字节状态（与 A12 SET_TRADE_STATE 同构，P-35 实证）。</summary>
public enum TradeState : byte
{
    /// <summary>未确认（初态；A12 线索 0）。</summary>
    Open = 0,
    /// <summary>已确认（A12 线索 1；60A1 实证 {1/0} 切换）。</summary>
    Confirmed = 1,
    /// <summary>整场取消（60A1 实证 {2}，0x4622E0）。</summary>
    Cancelled = 2,
    /// <summary>双方锁定（60A1 实证 {3}，0x7C9C3C）。</summary>
    Locked = 3,
}

/// <summary>交易会话状态变更结果（事件载荷；状态机纯内存，docs/design/12 §3.3 零持久化）。</summary>
public sealed record TradeStateChange(
    Guid InitiatorSessionId,
    Guid PartnerSessionId,
    TradeState State);

/// <summary>
/// 玩家交易状态机（11 号 P3 第一步；docs/design/12 §3.3）。会话对由发起方建立
/// （BeginTrade——发起/邀请流是 P-36 真机开口，接口先行）；CMD 26 单字节形态
/// 驱动转移：{1/0} 确认切换、{2} 取消（拆除会话对）、{3} 双方确认即锁定。
/// 值域 0/4 语义留真机。
/// </summary>
public sealed class TradeSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Guid> _partnerBySession = new();
    private readonly Dictionary<Guid, TradeState> _stateBySession = new();

    /// <summary>会话对当前是否成立。</summary>
    public bool TryGetPartner(Guid sessionId, out Guid partnerSessionId)
    {
        lock (_gate)
        {
            return _partnerBySession.TryGetValue(sessionId, out partnerSessionId!);
        }
    }

    public TradeState GetState(Guid sessionId)
    {
        lock (_gate)
        {
            return _stateBySession.GetValueOrDefault(sessionId, TradeState.Open);
        }
    }

    /// <summary>建立会话对（发起流落定后的入口；现无线上调用方）。</summary>
    public bool BeginTrade(Guid sessionIdA, Guid sessionIdB)
    {
        if (sessionIdA == sessionIdB)
        {
            return false;
        }

        lock (_gate)
        {
            if (_partnerBySession.ContainsKey(sessionIdA) || _partnerBySession.ContainsKey(sessionIdB))
            {
                return false;
            }

            _partnerBySession[sessionIdA] = sessionIdB;
            _partnerBySession[sessionIdB] = sessionIdA;
            _stateBySession[sessionIdA] = TradeState.Open;
            _stateBySession[sessionIdB] = TradeState.Open;
            return true;
        }
    }

    /// <summary>
    /// 应用一条 CMD 26 状态字节；返回双方的状态变更（同值幂等返回 null）。
    /// {2} 取消与 {3} 双方确认后拆除会话对。
    /// </summary>
    public TradeStateChange? ApplyStateByte(Guid sessionId, byte stateByte)
    {
        Guid partner;
        lock (_gate)
        {
            if (!_partnerBySession.TryGetValue(sessionId, out partner!))
            {
                return null;
            }

            switch (stateByte)
            {
                case 0 or 1:
                    var confirmed = stateByte == 1;
                    if (_stateBySession.GetValueOrDefault(sessionId, TradeState.Open)
                        == (confirmed ? TradeState.Confirmed : TradeState.Open))
                    {
                        return null;
                    }

                    _stateBySession[sessionId] = confirmed ? TradeState.Confirmed : TradeState.Open;
                    return new TradeStateChange(sessionId, partner, confirmed ? TradeState.Confirmed : TradeState.Open);
                case 2:
                    EndTradeLocked(sessionId, partner);
                    return new TradeStateChange(sessionId, partner, TradeState.Cancelled);
                case 3:
                    // 双锁：仅当双方均已确认时成立；成立即拆除（完成流随后）。
                    var self = _stateBySession.GetValueOrDefault(sessionId, TradeState.Open);
                    var other = _stateBySession.GetValueOrDefault(partner, TradeState.Open);
                    if (self != TradeState.Confirmed || other != TradeState.Confirmed)
                    {
                        return null;
                    }

                    EndTradeLocked(sessionId, partner);
                    return new TradeStateChange(sessionId, partner, TradeState.Locked);
                default:
                    return null;
            }
        }
    }

    /// <summary>断线清理：拆除涉及该会话的所有会话对，返回被拆对的另一端（无则空）。</summary>
    public Guid? EndTradeForSession(Guid sessionId)
    {
        lock (_gate)
        {
            if (!_partnerBySession.TryGetValue(sessionId, out var partner))
            {
                return null;
            }

            EndTradeLocked(sessionId, partner);
            return partner;
        }
    }

    private void EndTradeLocked(Guid sessionId, Guid partner)
    {
        _partnerBySession.Remove(sessionId);
        _partnerBySession.Remove(partner);
        _stateBySession.Remove(sessionId);
        _stateBySession.Remove(partner);
    }
}