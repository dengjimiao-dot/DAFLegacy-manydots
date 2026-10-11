using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// INCREASE_STATUS (CMD 32): consumes skill-reset items (River Lethe), experience books and permanent-status items through the persisted use transactions (07 §5 group 8).
/// </summary>
public sealed class IncreaseStatusHandler(IGameStore store,
    ItemCatalog itemCatalog,
    SkillCatalog skillCatalog,
    CharacterSpCatalog spCatalog,
    CharacterExperienceCatalog experienceCatalog,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private const ushort RiverLetheItemId = 3;
    private const ushort RiverLetheBellItemId = 28;

    private static int ToIncreaseStatusWireAmount(IncreaseStatusEffect effect) =>
        effect.Type is IncreaseStatusType.SkillPoints or IncreaseStatusType.Experience
            or IncreaseStatusType.RevivalCoins
            ? effect.Amount
            : checked(effect.Amount * 10);
    public ushort ProtocolId => (byte)CmdPacketType.INCREASE_STATUS;

    public string Name => "IncreaseStatus";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null || request.Body.Length < 4)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 17);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var sourceSlot = BinaryPrimitives.ReadUInt16LittleEndian(
            request.Body.AsSpan(2, sizeof(ushort)));
        if (!session.Inventory.Main.TryGetValue(sourceSlot, out var sourceItem))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 17);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected increase-status command for slot {SourceSlot}: the slot is empty.",
                sourceSlot);
            return;
        }

        if (sourceItem.ItemId is RiverLetheItemId or RiverLetheBellItemId)
        {
            var resetSkills = skillCatalog.CreateResetLayout(
                session.Character.Active.Job,
                session.Character.Active.GrowType,
                session.Skill.Current,
                CharacterPacketBuilders.InitialSkillsByJob.TryGetValue(
                    session.Character.Active.Job,
                    out var initialSkills)
                        ? initialSkills
                        : [],
                awakeningType: session.Character.Active.AwakeningType);
            var accumulatedLevelSkillPoints =
                spCatalog.CalculateTotalReward(session.Character.Active.Level);
            var resetResult = await store.UseSkillResetItemAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                sourceSlot,
                sourceItem.ItemId,
                accumulatedLevelSkillPoints,
                resetSkills,
                cancellationToken);
            if (!resetResult.Success || resetResult.Character is null)
            {
                var error = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 17);
                await context.Queue.SendAsync(error, Name, cancellationToken);
                RuntimeLogger.Warn(
                    "Rejected skill-reset item {ItemId} at slot {SourceSlot}: persisted state changed ({Failure}).",
                    sourceItem.ItemId,
                    sourceSlot,
                    resetResult.Failure);
                return;
            }

            session.Character.Active = resetResult.Character;
            session.Skill.CurrentSp = resetResult.ResetSkillPoints;
            session.Skill.Current = (session.Character.Active.Skills ?? [])
                .Where(skill => skill.Level > 0)
                .OrderBy(skill => skill.Slot)
                .ToList();
            session.Inventory.Main = (session.Character.Active.Inventory ?? [])
                .ToDictionary(item => item.Slot);

            // The reference server refreshes the complete skill tree before
            // acknowledging INCREASE_STATUS type 10.
            await senders.SendCurrentSkillInfoAsync(context, cancellationToken);
            var resetReply = SkillPacketBuilders.CreateIncreaseStatusReply(
                sourceSlot,
                statusType: (byte)IncreaseStatusType.SkillReset,
                amount: 1);
            await context.Queue.SendAsync(resetReply, Name, cancellationToken);
            RuntimeLogger.Info(
                "Used skill-reset item {ItemId} at slot {SourceSlot}; SP reset to level rewards {LevelSkillPoints} + bonus {BonusSp} = {SkillPoints}, restored skills={SkillCount}, preserved guild skills={GuildSkillCount}, stack remaining={RemainingCount}.",
                sourceItem.ItemId,
                sourceSlot,
                accumulatedLevelSkillPoints,
                session.Character.Active.BonusSp,
                session.Skill.CurrentSp,
                session.Skill.Current.Count,
                session.Skill.Current.Count(skill =>
                    skillCatalog.TryGetDefinition(
                        session.Character.Active.Job,
                        skill.SkillId,
                        out var definition)
                    && definition.IsGuildSkill),
                resetResult.RemainingItemCount);
            return;
        }

        if (!itemCatalog.TryGetDefinition(
                sourceItem.ItemId,
                out var sourceDefinition)
            || sourceDefinition.IncreaseStatus is not { Amount: > 0 })
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 17);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected increase-status command for slot {SourceSlot}: the slot does not contain a cached permanent-status item.",
                sourceSlot);
            return;
        }

        var increaseStatus = sourceDefinition.IncreaseStatus;
        if (increaseStatus.Type == IncreaseStatusType.Experience)
        {
            var useExperienceResult =
                await store.UseExperienceBookItemAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    sourceSlot,
                    sourceItem.ItemId,
                    checked((uint)increaseStatus.Amount),
                    experienceCatalog,
                    spCatalog,
                    cancellationToken);
            if (!useExperienceResult.Success
                || useExperienceResult.Character is null
                || useExperienceResult.Progression is null)
            {
                var error = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 17);
                await context.Queue.SendAsync(error, Name, cancellationToken);
                RuntimeLogger.Warn(
                    "Rejected experience book {ItemId} at slot {SourceSlot}: persisted item state changed or experience cannot be granted ({Failure}).",
                    sourceItem.ItemId,
                    sourceSlot,
                    useExperienceResult.Failure);
                return;
            }

            var progression = useExperienceResult.Progression;
            session.Character.Active = useExperienceResult.Character;
            session.Skill.CurrentSp = Math.Max(0, session.Character.Active.Sp);
            session.Inventory.Main = (session.Character.Active.Inventory ?? [])
                .ToDictionary(item => item.Slot);

            var experienceReply =
                SkillPacketBuilders.CreateIncreaseStatusReply(
                    sourceSlot,
                    statusType: (byte)IncreaseStatusType.Experience,
                    amount: checked((int)progression.GrantedExperience),
                    firstAuxiliaryValue: checked((ushort)Math.Min(
                        useExperienceResult.GrantedSkillPoints,
                        ushort.MaxValue)),
                    secondAuxiliaryValue: 0);
            await context.Queue.SendAsync(experienceReply, Name, cancellationToken);

            if (progression.LeveledUp)
            {
                await senders.SendCurrentSkillInfoAsync(context, cancellationToken);
                await senders.ValidateAndCorrectCharacterStatsAsync(
    context,
                    "experience-book level-up");
            }
            else
            {
                await senders.SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
            }

            if (progression.LeveledUp)
            {
                await senders.SendAcceptableQuestListAsync(context, cancellationToken);
            }

            RuntimeLogger.Info(
                "Used experience book {ItemId} at slot {SourceSlot}; experience=+{Experience}, cumulative={CumulativeExperience}, level={PreviousLevel}->{Level}, SP=+{SkillPoints}, stack remaining={RemainingCount}.",
                sourceItem.ItemId,
                sourceSlot,
                progression.GrantedExperience,
                progression.Experience,
                progression.PreviousLevel,
                progression.Level,
                useExperienceResult.GrantedSkillPoints,
                useExperienceResult.RemainingItemCount);
            return;
        }

        var useResult = await store.UseIncreaseStatusItemAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            sourceSlot,
            sourceItem.ItemId,
            increaseStatus,
            cancellationToken);
        if (!useResult.Success || useResult.Character is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 17);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected permanent-status item {ItemId} at slot {SourceSlot}: persisted item state changed ({Failure}).",
                sourceItem.ItemId,
                sourceSlot,
                useResult.Failure);
            return;
        }

        session.Character.Active = useResult.Character;
        session.Skill.CurrentSp = Math.Max(0, session.Character.Active.Sp);
        session.Inventory.Main = (session.Character.Active.Inventory ?? [])
            .ToDictionary(item => item.Slot);
        var responseAmount = ToIncreaseStatusWireAmount(
            increaseStatus);
        var increaseStatusReply = SkillPacketBuilders.CreateIncreaseStatusReply(
            sourceSlot,
            statusType: (byte)increaseStatus.Type,
            amount: responseAmount);
        await context.Queue.SendAsync(increaseStatusReply, Name, cancellationToken);
        if (increaseStatus.Type == IncreaseStatusType.RevivalCoins)
        {
            // DF2008 consumes the voucher for native coin type 13 without
            // a dedicated effect message. NOTI14 updates the balance and
            // supplies its existing coin-gain notice; type 11 is guild score.
            var coins = session.Inventory.Main[CharacterInventoryLayout.RevivalCoinSlot];
            var coinUpdate = InventoryPacketBuilders.CreateUpdateItemList(0,
                [builders.ToGameInventoryEntry(coins)]);
            await context.Queue.SendAsync(coinUpdate, Name, cancellationToken);
            RuntimeLogger.Info(
                "Used revival-coin voucher {ItemId} at slot {SourceSlot}; granted={GrantedCoins}, balance={CoinBalance}, stack remaining={RemainingCount}.",
                sourceItem.ItemId,
                sourceSlot,
                increaseStatus.Amount,
                coins.CountOrValue,
                useResult.RemainingItemCount);
            return;
        }

        RuntimeLogger.Info(
            "Used permanent-status item {ItemId} at slot {SourceSlot}; type={StatusType}, persisted amount={Amount}, response amount={ResponseAmount}, remaining SP={RemainingSp}, cumulative bonuses=[SP:{BonusSp}, HP:{BonusHp}, MP:{BonusMp}, STR:{BonusStrength}, VIT:{BonusVitality}, INT:{BonusIntelligence}, SPI:{BonusSpirit}, MOVE:{BonusMovementSpeed}, ALLR:{BonusAllResistance}], stack remaining={RemainingCount}.",
            sourceItem.ItemId,
            sourceSlot,
            increaseStatus.Type,
            increaseStatus.Amount,
            responseAmount,
            session.Skill.CurrentSp,
            session.Character.Active.BonusSp,
            session.Character.Active.BonusMaximumHp,
            session.Character.Active.BonusMaximumMp,
            session.Character.Active.BonusStrength,
            session.Character.Active.BonusVitality,
            session.Character.Active.BonusIntelligence,
            session.Character.Active.BonusSpirit,
            session.Character.Active.BonusMovementSpeed,
            session.Character.Active.BonusAllElementResistance,
            useResult.RemainingItemCount);
    }
}
