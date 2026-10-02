namespace DAFLegacy.Server;

/// <summary>
/// 持久化门面：游戏状态的全部读写入口（docs/design/08-sqlite-storage.md G3）。
/// JSON 与 SQLite 两种实现共用同一公开面，EntranceService、后台服务与 Admin API
/// 因此不感知存储形态。签名随 <see cref="GameStoreCore"/> 一同演进：新增或修改
/// 公开方法时两处必须同步，由编译期的隐式实现检查兜底。
/// </summary>
public interface IGameStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    // ---- 社交关系（11 号 P2）：好友单向边与角色级黑名单（docs/design/12 §3.2）----
    Task<SocialFriendEntry[]> GetFriendsAsync(
        Guid characterId,
        CancellationToken cancellationToken = default);
    Task<Guid[]> GetFriendOwnerIdsAsync(
        Guid characterId,
        CancellationToken cancellationToken = default);
    Task<(bool Ok, string? Error)> AddFriendAsync(
        Guid ownerCharacterId,
        string friendName,
        CancellationToken cancellationToken = default);
    Task<(bool Ok, string? Error)> RemoveFriendAsync(
        Guid ownerCharacterId,
        string friendName,
        CancellationToken cancellationToken = default);
    Task<SocialBlacklistRow[]> GetBlacklistAsync(
        Guid ownerCharacterId,
        CancellationToken cancellationToken = default);
    Task<(bool Ok, string? Error)> AddBlacklistAsync(
        Guid ownerCharacterId,
        string targetName,
        CancellationToken cancellationToken = default);
    Task<(bool Ok, string? Error)> RemoveBlacklistAsync(
        Guid ownerCharacterId,
        string targetName,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> FindCharacterByNameAsync(
        string name,
        CancellationToken cancellationToken = default);
    Task<uint> GetCharacterAccountUidAsync(
        Guid characterId,
        CancellationToken cancellationToken = default);
    Task<PublicAccountRecord[]> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task<AuthenticatedAccount?> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default);
    Task<AccountPasswordUpdateResult> ChangePasswordAsync(
        string userName,
        ChangeAccountPasswordRequest request,
        CancellationToken cancellationToken = default);
    Task<AccountPasswordUpdateResult> ResetPasswordAsync(
        string userName,
        ResetAccountPasswordRequest request,
        CancellationToken cancellationToken = default);
    Task<AuthenticatedAccount?> FindAccountAsync(
        string userName,
        CancellationToken cancellationToken = default);
    Task<AuthenticatedAccount?> GetAccountAsync(
        uint accountUid,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord[]> GetCharactersAsync(
        string userName,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord[]> GetCharactersAsync(
        uint accountUid,
        CancellationToken cancellationToken = default);
    Task<CharacterRosterEntry[]> GetCharacterRosterAsync(
        uint accountUid,
        CancellationToken cancellationToken = default);
    Task<(bool Created, PublicAccountRecord? Account, string? Error)> CreateAccountAsync(
        CreateAccountRequest request,
        CancellationToken cancellationToken = default);
    Task<(bool Created, CharacterRecord? Character, string? Error)> CreateCharacterAsync(
        string userName,
        CreateCharacterRequest request,
        CancellationToken cancellationToken = default);
    Task<(bool Created, CharacterRecord? Character, string? Error)> CreateCharacterAsync(
        uint accountUid,
        CreateCharacterRequest request,
        CancellationToken cancellationToken = default);
    Task<(bool Deleted, CharacterRecord? Character, string? Error)> DeleteCharacterAsync(
        string userName,
        int slot,
        CancellationToken cancellationToken = default);
    Task<(bool Deleted, CharacterRecord? Character, string? Error)> DeleteCharacterAsync(
        uint accountUid,
        int slot,
        CancellationToken cancellationToken = default);
    Task<(bool Replaced, int CharacterCount, string? Error)> ReplaceAllCharactersAsync(
        string targetUserName,
        IEnumerable<CharacterRecord> characters,
        CancellationToken cancellationToken = default);
    Task<int> AddCashToAllCharactersAsync(
        int amount,
        CancellationToken cancellationToken = default);
    Task<ClientCeraTopUpResult> GrantCeraToAccountAsync(
        string accountName,
        int amount,
        CancellationToken cancellationToken = default);
    Task<ClientDiamondGrantResult> GrantDiamondToAccountAsync(
        string accountName,
        int days,
        CancellationToken cancellationToken = default);
    Task<ClientPremiumGrantResult> GrantPremiumServiceToAccountAsync(
        string accountName,
        byte serviceType,
        int days,
        CancellationToken cancellationToken = default);
    Task<PremiumServiceState> GetPremiumServiceAsync(
        string accountName,
        CancellationToken cancellationToken = default);
    Task<PremiumServiceEntryState[]> GetPremiumServicesAsync(
        string accountName,
        CancellationToken cancellationToken = default);
    Task<CharacterMailRecord[]> GetCharacterMailsAsync(
        Guid characterId,
        CancellationToken cancellationToken = default);
    Task<(bool Created, CharacterMailRecord? Mail, string? Error)>
        SendCharacterMailAsync(
            Guid characterId,
            CreateCharacterMailRequest request,
            CancellationToken cancellationToken = default);
    Task<CharacterMailSendResult> SendCharacterMailFromCharacterAsync(
        string userName,
        Guid senderCharacterId,
        string recipientName,
        CreateCharacterMailRequest request,
        int senderGold,
        IEnumerable<CharacterItemRecord> senderInventory,
        IEnumerable<CharacterItemRecord> senderEquipment,
        CancellationToken cancellationToken = default);
    Task<int> AcknowledgeCharacterMailNotificationsAsync(
        Guid characterId,
        CancellationToken cancellationToken = default);
    Task<CharacterMailClaimResult?> ClaimCharacterMailAsync(
        string userName,
        Guid characterId,
        uint mailId,
        int gold,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equipment,
        CancellationToken cancellationToken = default,
        IEnumerable<CharacterItemRecord>? avatarInventory = null,
        IEnumerable<CharacterItemRecord>? creatureInventory = null,
        ushort? warehouseCapacity = null,
        ushort? expectedWarehouseCapacity = null);
    Task<bool> SetCharacterMailStateAsync(
        Guid characterId,
        uint mailId,
        ushort state,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteCharacterMailAsync(
        Guid characterId,
        uint mailId,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> SaveCharacterInventoryAsync(
        string userName,
        Guid characterId,
        int gold,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equipment,
        CancellationToken cancellationToken = default,
        IEnumerable<CreateCharacterMailRequest>? generatedMails = null,
        ushort? warehouseCapacity = null,
        ushort? expectedWarehouseCapacity = null);
    Task<CharacterRecord?> CommitCompoundItemAsync(
        string userName,
        Guid characterId,
        int expectedGold,
        IEnumerable<CharacterItemRecord> expectedInventory,
        int remainingGold,
        IEnumerable<CharacterItemRecord> plannedInventory,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> CommitItemAttributeModificationAsync(
        string userName,
        Guid characterId,
        IEnumerable<CharacterItemRecord> expectedInventory,
        IEnumerable<CharacterItemRecord> expectedEquipment,
        IEnumerable<CharacterItemRecord> plannedInventory,
        IEnumerable<CharacterItemRecord> plannedEquipment,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> SaveCharacterItemSpacesAsync(
        string userName,
        Guid characterId,
        int gold,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equipment,
        IEnumerable<CharacterItemRecord> warehouse,
        CancellationToken cancellationToken = default,
        IEnumerable<CreateCharacterMailRequest>? generatedMails = null,
        IEnumerable<CharacterItemRecord>? avatarInventory = null,
        IEnumerable<CharacterItemRecord>? creatureInventory = null,
        ushort? warehouseCapacity = null,
        ushort? expectedWarehouseCapacity = null);
    Task<CharacterRecord?> SaveCharacterCeraShopPurchaseAsync(
        string userName,
        Guid characterId,
        int cash,
        int gold,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equipment,
        CancellationToken cancellationToken = default,
        IEnumerable<CreateCharacterMailRequest>? generatedMails = null,
        IEnumerable<CharacterItemRecord>? avatarInventory = null,
        IEnumerable<CharacterItemRecord>? creatureInventory = null,
        IEnumerable<PremiumServicePurchaseGrant>? premiumServiceGrants = null,
        uint? victoryPoints = null,
        int? expectedCash = null,
        ushort? warehouseCapacity = null,
        ushort? expectedWarehouseCapacity = null);
    Task<CharacterRecord?> SaveCharacterSkillsAsync(
        string userName,
        Guid characterId,
        int remainingSp,
        IEnumerable<CharacterSkillRecord> skills,
        CancellationToken cancellationToken = default);
    Task<IncreaseStatusItemUseResult> UseIncreaseStatusItemAsync(
        string userName,
        Guid characterId,
        ushort sourceSlot,
        ushort expectedItemId,
        IncreaseStatusEffect effect,
        CancellationToken cancellationToken = default);
    Task<ExperienceBookItemUseResult> UseExperienceBookItemAsync(
        string userName,
        Guid characterId,
        ushort sourceSlot,
        ushort expectedItemId,
        uint experienceAmount,
        CharacterExperienceCatalog characterExperienceCatalog,
        CharacterSpCatalog spCatalog,
        CancellationToken cancellationToken = default);
    Task<ExperienceBookItemUseResult> UseLevelUpCouponItemAsync(
        string userName,
        Guid characterId,
        ushort sourceSlot,
        ushort expectedItemId,
        CharacterExperienceCatalog characterExperienceCatalog,
        CharacterSpCatalog spCatalog,
        CancellationToken cancellationToken = default);
    Task<PvpExperienceItemUseResult> UsePvpExperienceItemAsync(
        string userName,
        Guid characterId,
        ushort sourceSlot,
        ushort expectedItemId,
        uint experienceAmount,
        PvpExperienceCatalog pvpCatalog,
        CancellationToken cancellationToken = default);
    Task<SkillResetItemUseResult> UseSkillResetItemAsync(
        string userName,
        Guid characterId,
        ushort sourceSlot,
        ushort expectedItemId,
        int accumulatedLevelSkillPoints,
        IReadOnlyList<CharacterSkillRecord> resetSkills,
        CancellationToken cancellationToken = default);
    Task<FatigueRecoveryItemUseResult> UseFatigueRecoveryItemAsync(
        string userName,
        Guid characterId,
        ushort sourceSlot,
        ushort expectedItemId,
        ushort maximumFatigue,
        ushort maximumRemainingFatigue,
        ushort recoveryAmount,
        CancellationToken cancellationToken = default);
    Task<int> NormalizePvpGradesAsync(
        PvpExperienceCatalog pvpCatalog,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> SaveCharacterQuestsAsync(
        string userName,
        Guid characterId,
        IEnumerable<CharacterQuestRecord> quests,
        IEnumerable<ushort> completedQuestIds,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> CompleteCharacterTutorialAsync(
        string userName,
        Guid characterId,
        CharacterQuestRecord? tutorialQuest = null,
        int? level = null,
        uint? experience = null,
        int? skillPoints = null,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> SaveCharacterQuestProgressAsync(
        string userName,
        Guid characterId,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterQuestRecord> quests,
        IEnumerable<ushort> completedQuestIds,
        CancellationToken cancellationToken = default,
        ushort? warehouseCapacity = null,
        ushort? expectedWarehouseCapacity = null,
        IEnumerable<ushort>? unlockedDungeonIds = null);
    Task<CharacterRecord?> SaveCharacterProgressionAsync(
        string userName,
        Guid characterId,
        int level,
        uint experience,
        int skillPoints,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> SaveCharacterDungeonUnlocksAsync(
        string userName,
        Guid characterId,
        IEnumerable<ushort> unlockedDungeonIds,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> SaveCharacterDungeonProgressAsync(
        string userName,
        Guid characterId,
        IEnumerable<CharacterDungeonProgressRecord> dungeonProgress,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> UnlockNextDungeonDifficultyAsync(
        string userName,
        Guid characterId,
        ushort dungeonId,
        byte clearedDifficulty,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> SetCharacterUsedFatigueAsync(
        Guid characterId,
        ushort usedFatigue,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> SetCharacterDungeonDifficultyAsync(
        Guid characterId,
        ushort dungeonId,
        byte maximumDifficulty,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> SetCharacterWeaknessRecoveryAsync(
        Guid characterId,
        byte recovery,
        CancellationToken cancellationToken = default);
    Task<CharacterWeaknessRecoveryUpdate[]>
        AdvanceCharacterWeaknessRecoveryAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default);
    Task<WeaknessRecoveryResult> RecoverCharacterWeaknessAsync(
        Guid characterId,
        int recoveryCost,
        CancellationToken cancellationToken = default);
    Task<DungeonRoomFatigueConsumptionResult>
        ConsumeDungeonRoomFatigueAsync(
            Guid characterId,
            ushort maximumFatigue,
            CancellationToken cancellationToken = default);
    Task<FatigueResetResult> ResetFatigueForCurrentDayAsync(
        DateTimeOffset localNow,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> SaveCharacterLocationAsync(
        string userName,
        Guid characterId,
        byte townId,
        byte areaId,
        short x,
        short y,
        byte direction,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> CompleteCharacterQuestAsync(
        string userName,
        Guid characterId,
        ushort questId,
        int level,
        uint experience,
        int skillPoints,
        int gold,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterQuestRecord> quests,
        IEnumerable<ushort> completedQuestIds,
        CancellationToken cancellationToken = default,
        IEnumerable<CreateCharacterMailRequest>? generatedMails = null,
        IEnumerable<CharacterItemRecord>? creatureInventory = null,
        IEnumerable<CharacterSkillRecord>? skills = null,
        int? awakeningType = null,
        uint? victoryPoints = null,
        ushort? warehouseCapacity = null,
        ushort? expectedWarehouseCapacity = null,
        int? growType = null,
        IEnumerable<ushort>? unlockedDungeonIds = null);
    Task<int> MigrateLegacyQuestRewardsAsync(
        QuestCatalog questCatalog,
        CharacterExperienceCatalog experienceCatalog,
        CharacterSpCatalog spCatalog,
        ItemCatalog itemCatalog,
        CancellationToken cancellationToken = default,
        SkillCatalog? skillCatalog = null);
}
