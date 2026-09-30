using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// COMPOUND_AVATAR (CMD 102): avatar compounding through the avatar table (07 §5 group 11).
/// </summary>
public sealed class CompoundAvatarHandler(JsonGameStore store,
    AvatarCompoundCatalog avatarCompoundCatalog,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.COMPOUND_AVATAR;

    public string Name => "CompoundAvatar";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || session.Dungeon.CurrentDungeonId != 0
            || !CraftPacketBuilders.TryParseCompoundAvatarCommand(
                request.Body,
                out var compoundCommand))
        {
            var error =
                CraftPacketBuilders.CreateCompoundAvatarFailure();
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        if (!AvatarCompoundPlanner.TryCreate(
                session.Inventory.Main,
                session.Inventory.Avatar,
                session.Character.Active.Job,
                compoundCommand.MaterialSlot,
                compoundCommand.FirstAvatarItemId,
                compoundCommand.SecondAvatarItemId,
                compoundCommand.SelectedOption,
                compoundCommand.FirstAvatarSlot,
                compoundCommand.SecondAvatarSlot,
                avatarCompoundCatalog,
                GameRandomSource.Shared,
                out var compoundPlan,
                out var compoundFailure))
        {
            var error =
                CraftPacketBuilders.CreateCompoundAvatarFailure();
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected avatar compound for job {Job}, material slot {MaterialSlot}, items {FirstItemId}/{SecondItemId}, slots {FirstSlot}/{SecondSlot}, option {Option}: {Failure}.",
                session.Character.Active.Job,
                compoundCommand.MaterialSlot,
                compoundCommand.FirstAvatarItemId,
                compoundCommand.SecondAvatarItemId,
                compoundCommand.FirstAvatarSlot,
                compoundCommand.SecondAvatarSlot,
                compoundCommand.SelectedOption,
                compoundFailure);
            return;
        }

        var savedCompound =
            await store.SaveCharacterItemSpacesAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                session.Currency.Gold,
                compoundPlan.MainInventory.Values,
                session.Inventory.Equipped.Values,
                session.Inventory.Warehouse.Values,
                cancellationToken,
                avatarInventory: compoundPlan.AvatarInventory.Values,
                creatureInventory: session.Inventory.Creature.Values);
        if (savedCompound is null)
        {
            var error =
                CraftPacketBuilders.CreateCompoundAvatarFailure();
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var warehouseCapacityChanged =
            senders.AdoptSavedWarehouseUpgradeState(context, savedCompound);
        session.Inventory.Equipped = (savedCompound.Equipment ?? [])
            .ToDictionary(item => item.Slot);
        session.Inventory.Avatar = (savedCompound.AvatarInventory ?? [])
            .ToDictionary(item => item.Slot);
        session.Inventory.Creature = (savedCompound.CreatureInventory ?? [])
            .ToDictionary(item => item.Slot);
        session.Currency.Gold = savedCompound.Gold;
        if (warehouseCapacityChanged)
        {
            await senders.SendWarehouseStateAsync(context, cancellationToken);
        }

        var compoundReply =
            CraftPacketBuilders.CreateCompoundAvatarReply(
                [
                    new GameAvatarCompoundConsumption(
                        0,
                        compoundPlan.MaterialSlot,
                        compoundPlan.MaterialRemaining),
                    new GameAvatarCompoundConsumption(
                        1,
                        compoundPlan.RequestedFirstSlot,
                        0),
                    new GameAvatarCompoundConsumption(
                        1,
                        compoundPlan.RequestedSecondSlot,
                        0)
                ],
                compoundPlan.ResolvedFirstSlot,
                compoundPlan.ResultItemId,
                remainingSeconds: 0,
                selectedOption: compoundPlan.SelectedOption);
        await context.Queue.SendAsync(compoundReply, Name, cancellationToken);

        var inventoryRefresh = builders.CreateMainInventoryPacket(
            session.Currency.Gold,
            session.Currency.VictoryPoints,
            session.Inventory.Main.Values);
        await context.Queue.SendAsync(inventoryRefresh, Name, cancellationToken);

        var avatarRefresh =
            builders.CreateAvatarInventoryPacket(session.Inventory.Avatar.Values);
        await context.Queue.SendAsync(avatarRefresh, Name, cancellationToken);
        RuntimeLogger.Info(
            "Compounded avatars for job {Job}, part {Part}: items {FirstItemId}/{SecondItemId} at {FirstSlot}/{SecondSlot} -> {ResultItemId} at {ResultSlot}, option={Option}, rare={Rare}, rate={RareRate}/{RateUpperBound}, material={MaterialItemId}x{MaterialCount}, remaining={MaterialRemaining}.",
            session.Character.Active.Job,
            compoundPlan.ResultPart,
            compoundCommand.FirstAvatarItemId,
            compoundCommand.SecondAvatarItemId,
            compoundPlan.ResolvedFirstSlot,
            compoundPlan.ResolvedSecondSlot,
            compoundPlan.ResultItemId,
            compoundPlan.ResolvedFirstSlot,
            compoundPlan.SelectedOption,
            compoundPlan.IsRare,
            compoundPlan.RareRate,
            AvatarCompoundCatalog.RareRateUpperBound,
            compoundPlan.MaterialItemId,
            compoundPlan.MaterialConsumed,
            compoundPlan.MaterialRemaining);
    }
}
