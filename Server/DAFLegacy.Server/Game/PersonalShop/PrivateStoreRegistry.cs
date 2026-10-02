namespace DAFLegacy.Server;

/// <summary>
/// 个人商店注册表（11 号 P4；docs/design/12 §3.4）。内存态对齐 PartyManager 形态：
/// 每角色至多一店（owner CharacterNo 主键）；开摊 = 88 建占位 → 90 挂名完成；
/// 上架物品按 12 号「物理行引用」原则持久化（item_uid 不移主）。
/// 价格通道与上架物品锁定形态（候选：CMD 26 锁定体）值域待真机（P-36），本版
/// 仅持久化摊位头（标题/开张态），物品目录暂驻内存。
/// </summary>
public sealed class PrivateStoreRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PrivateStore> _storesByOwner = new();

    public sealed class PrivateStore
    {
        public required Guid OwnerCharacterId { get; init; }
        public required ushort OwnerUserId { get; set; }
        public string Title { get; set; } = string.Empty;
        public bool Open { get; set; }
        public List<PrivateStoreListing> Listings { get; } = [];
    }

    public sealed record PrivateStoreListing(
        uint ItemUid,
        ushort Slot,
        uint ItemId,
        ushort CountOrPriceTail,
        uint PriceGold,
        byte Count);

    /// <summary>CMD 88：建占位摊（重复建摊幂等返回既有摊）。</summary>
    public PrivateStore Create(Guid ownerCharacterId, ushort ownerUserId)
    {
        lock (_gate)
        {
            if (_storesByOwner.TryGetValue(ownerCharacterId, out var existing))
            {
                existing.OwnerUserId = ownerUserId;
                return existing;
            }

            var store = new PrivateStore
            {
                OwnerCharacterId = ownerCharacterId,
                OwnerUserId = ownerUserId,
            };
            _storesByOwner[ownerCharacterId] = store;
            return store;
        }
    }

    /// <summary>CMD 90：挂名并开张。</summary>
    public bool Complete(Guid ownerCharacterId, string title)
    {
        lock (_gate)
        {
            if (!_storesByOwner.TryGetValue(ownerCharacterId, out var store)
                || string.IsNullOrEmpty(title))
            {
                return false;
            }

            store.Title = title;
            store.Open = true;
            return true;
        }
    }

    /// <summary>CMD 89：收摊并清空目录。返回被收的摊（无则 null）。</summary>
    public PrivateStore? Remove(Guid ownerCharacterId)
    {
        lock (_gate)
        {
            if (!_storesByOwner.Remove(ownerCharacterId, out var store))
            {
                return null;
            }

            store.Open = false;
            store.Listings.Clear();
            return store;
        }
    }

    public PrivateStore? FindByCharacterId(Guid ownerCharacterId)
    {
        lock (_gate)
        {
            return _storesByOwner.TryGetValue(ownerCharacterId, out var store) ? store : null;
        }
    }

    public PrivateStore? FindByUserId(ushort ownerUserId)
    {
        lock (_gate)
        {
            foreach (var store in _storesByOwner.Values)
            {
                if (store.OwnerUserId == ownerUserId && store.Open)
                {
                    return store;
                }
            }
        }

        return null;
    }

    /// <summary>启动收摊（12 号 §3.4：is_open 归零、目录保留——由存储层在启动时调用）。</summary>
    public int CloseAll()
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var store in _storesByOwner.Values)
            {
                if (store.Open)
                {
                    store.Open = false;
                    count++;
                }
            }

            return count;
        }
    }
}
