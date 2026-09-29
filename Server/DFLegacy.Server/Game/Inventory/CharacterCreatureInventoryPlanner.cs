namespace DFLegacy.Server;

public static class CharacterCreatureInventoryLayout
{
    public const ushort CreatureBagStart = 0;
    public const ushort CreatureBagEnd = 140;
    public const ushort ArtifactBagStart = 140;
    public const ushort ArtifactBagEnd = 189;
    public const ushort StackableBagStart = 189;
    public const ushort StackableBagEnd = 238;
    public const ushort EquippedCreatureSlot = 238;
    public const ushort EquippedArtifactRedSlot = 239;
    public const ushort EquippedArtifactBlueSlot = 240;
    public const ushort EquippedArtifactGreenSlot = 241;
    public const ushort Capacity = 242;
    public const ushort ClientEquippedCreatureSlot = 19;
    public const ushort ClientEquippedArtifactRedSlot = 20;
    public const ushort ClientEquippedArtifactBlueSlot = 21;
    public const ushort ClientEquippedArtifactGreenSlot = 22;

    public static bool IsInventorySlot(ushort slot) => slot < Capacity;

    public static bool IsEquippedSlot(ushort slot) =>
        slot is >= EquippedCreatureSlot and < Capacity;

    public static bool IsCreature(ItemDefinition definition) =>
        definition.ScriptKind == ItemScriptKind.Equipment
        && definition.TypeTag == "creature";

    public static bool IsArtifact(ItemDefinition definition) =>
        definition.ScriptKind == ItemScriptKind.Equipment
        && definition.TypeTag.StartsWith("artifact ", StringComparison.Ordinal);

    public static bool IsCreatureStackable(ItemDefinition definition) =>
        definition.ScriptKind == ItemScriptKind.Stackable
        && definition.TypeTag is "creature" or "feed";

    public static bool IsCreatureRenameCard(ItemDefinition definition) =>
        definition.ScriptKind == ItemScriptKind.Stackable
        && definition.TypeTag == "creature"
        && definition.ScriptPath.Replace('\\', '/').EndsWith(
            "/rename_card.stk",
            StringComparison.OrdinalIgnoreCase);

    public static bool TryGetBagRange(
        ItemDefinition definition,
        out ushort start,
        out ushort end)
    {
        (start, end) = definition switch
        {
            _ when IsCreature(definition) => (CreatureBagStart, CreatureBagEnd),
            _ when IsArtifact(definition) => (ArtifactBagStart, ArtifactBagEnd),
            _ when IsCreatureStackable(definition) =>
                (StackableBagStart, StackableBagEnd),
            _ => ((ushort)0, (ushort)0)
        };
        return end > start;
    }

    public static bool TryGetEquippedSlot(
        ItemDefinition definition,
        out ushort slot)
    {
        slot = definition.TypeTag switch
        {
            "creature" when definition.ScriptKind == ItemScriptKind.Equipment
                && definition.CreatureSubType != 1 =>
                EquippedCreatureSlot,
            "artifact red" => EquippedArtifactRedSlot,
            "artifact blue" => EquippedArtifactBlueSlot,
            "artifact green" => EquippedArtifactGreenSlot,
            _ => ushort.MaxValue
        };
        return slot != ushort.MaxValue;
    }

    public static bool IsCompatibleSlot(ItemDefinition definition, ushort slot)
    {
        if (TryGetBagRange(definition, out var start, out var end)
            && slot >= start
            && slot < end)
        {
            return true;
        }

        return TryGetEquippedSlot(definition, out var equippedSlot)
            && slot == equippedSlot;
    }
}

public sealed record CharacterCreatureInventoryPlacementPlan(
    ushort DestinationSlot,
    Dictionary<ushort, CharacterItemRecord> Inventory,
    IReadOnlyList<ushort> ChangedSlots);

public sealed record CharacterCreatureInventoryNormalizationPlan(
    Dictionary<ushort, CharacterItemRecord> Inventory,
    IReadOnlyList<CharacterMailAttachmentRecord> OverflowItems,
    bool Changed);

public sealed record CharacterCreatureHatchPlan(
    ushort Slot,
    ushort EggItemId,
    ushort CreatureItemId,
    Dictionary<ushort, CharacterItemRecord> Inventory);

public sealed record CharacterCreatureRenamePlan(
    ushort CardSlot,
    uint CreatureUid,
    string CreatureName,
    Dictionary<ushort, CharacterItemRecord> Inventory);

public sealed record CharacterCreatureEvolutionPlan(
    ushort Slot,
    ushort SourceItemId,
    ushort EvolvedItemId,
    uint CreatureUid,
    Dictionary<ushort, CharacterItemRecord> Inventory);

public static class CharacterCreatureInventoryPlanner
{
    public static bool TryEvolve(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        QuestDefinition quest,
        ItemCatalog catalog,
        out CharacterCreatureEvolutionPlan plan,
        out string failure)
    {
        plan = null!;
        failure = string.Empty;
        if (quest.RewardType != "creature evolution"
            || quest.CreatureKind < 0
            || quest.EvolutionCreatureKind < 0)
        {
            failure = "the quest does not define a Creature evolution reward";
            return false;
        }

        var source = inventory.Values
            .Where(item => item.CountOrValue != 0
                && (item.CreatureLevel ?? 1) >= Math.Max(1, quest.CreatureLevel)
                && catalog.TryGetDefinition(item.ItemId, out var definition)
                && CharacterCreatureInventoryLayout.IsCreature(definition)
                && definition.CreatureSubType != 1
                && definition.CreatureSpecies == quest.CreatureKind)
            .OrderByDescending(item =>
                item.Slot == CharacterCreatureInventoryLayout.EquippedCreatureSlot)
            .ThenBy(item => item.Slot)
            .FirstOrDefault();
        if (source is null)
        {
            failure = $"no level-{quest.CreatureLevel} Creature species {quest.CreatureKind} is available";
            return false;
        }

        var evolvedDefinition = catalog.Definitions.Values
            .Where(definition => CharacterCreatureInventoryLayout.IsCreature(definition)
                && definition.CreatureSubType != 1
                && definition.CreatureSpecies == quest.EvolutionCreatureKind)
            .OrderBy(definition => definition.Id)
            .FirstOrDefault();
        if (evolvedDefinition is null)
        {
            failure = $"Creature species {quest.EvolutionCreatureKind} has no usable item definition";
            return false;
        }

        var planned = inventory.ToDictionary(entry => entry.Key, entry => entry.Value);
        planned[source.Slot] = source with
        {
            ItemId = evolvedDefinition.Id,
            SealState = 0
        };
        plan = new CharacterCreatureEvolutionPlan(
            source.Slot,
            source.ItemId,
            evolvedDefinition.Id,
            source.CountOrValue,
            planned);
        return true;
    }

    public static bool TryRename(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        ushort cardSlot,
        string requestedName,
        ItemCatalog catalog,
        out CharacterCreatureRenamePlan plan,
        out string failure)
    {
        plan = null!;
        failure = string.Empty;
        if (!inventory.TryGetValue(cardSlot, out var renameCard)
            || renameCard.CountOrValue == 0
            || !catalog.TryGetDefinition(renameCard.ItemId, out var cardDefinition)
            || !CharacterCreatureInventoryLayout.IsCreatureRenameCard(cardDefinition))
        {
            failure = "the Creature rename-card slot is empty or invalid";
            return false;
        }

        if (!inventory.TryGetValue(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                out var creature)
            || creature.CountOrValue == 0
            || !catalog.TryGetDefinition(creature.ItemId, out var creatureDefinition)
            || !CharacterCreatureInventoryLayout.IsCreature(creatureDefinition)
            || creatureDefinition.CreatureSubType == 1)
        {
            failure = "there is no equipped Creature to rename";
            return false;
        }

        var creatureName = string.Equals(
            requestedName,
            "没有名字",
            StringComparison.Ordinal)
            ? creatureDefinition.Name
            : requestedName.Trim();
        if (string.IsNullOrWhiteSpace(creatureName))
        {
            failure = "the Creature name is empty";
            return false;
        }

        var planned = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (renameCard.CountOrValue == 1)
        {
            planned.Remove(cardSlot);
        }
        else
        {
            planned[cardSlot] = renameCard with
            {
                CountOrValue = renameCard.CountOrValue - 1
            };
        }

        planned[CharacterCreatureInventoryLayout.EquippedCreatureSlot] = creature with
        {
            CreatureName = creatureName
        };
        plan = new CharacterCreatureRenamePlan(
            cardSlot,
            creature.CountOrValue,
            creatureName,
            planned);
        return true;
    }

    public static bool TryHatch(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        ushort slot,
        ItemCatalog catalog,
        out CharacterCreatureHatchPlan plan,
        out string failure)
    {
        plan = null!;
        failure = string.Empty;
        if (!CharacterCreatureInventoryLayout.IsInventorySlot(slot)
            || !inventory.TryGetValue(slot, out var egg))
        {
            failure = "the Creature egg slot is empty or invalid";
            return false;
        }

        if (!catalog.TryGetDefinition(egg.ItemId, out var eggDefinition)
            || !CharacterCreatureInventoryLayout.IsCreature(eggDefinition)
            || eggDefinition.CreatureSubType != 1
            || eggDefinition.CreatureOutputIndex is not (> 0 and <= ushort.MaxValue))
        {
            failure = "the selected Creature item is not a hatchable egg";
            return false;
        }

        var outputItemId = checked((ushort)eggDefinition.CreatureOutputIndex.Value);
        if (!catalog.TryGetDefinition(outputItemId, out var outputDefinition)
            || !CharacterCreatureInventoryLayout.IsCreature(outputDefinition)
            || outputDefinition.CreatureSubType == 1)
        {
            failure = "the Creature egg output item is missing or invalid";
            return false;
        }

        var planned = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var creature = NormalizeCreatureInstance(
            egg with
            {
                ItemId = outputItemId,
                SealState = 0,
                Durability = 0,
                CreatureStomach = 100,
                CreatureStomachRemainderSeconds = 0,
                CreatureExperience = 0,
                CreatureLevel = 1,
                CreatureName = outputDefinition.Name,
                CreatureNoCharge = false
            },
            planned
                .Where(pair => pair.Key != slot)
                .ToDictionary(pair => pair.Key, pair => pair.Value),
            outputDefinition);
        planned[slot] = creature with { Slot = slot };
        plan = new CharacterCreatureHatchPlan(
            slot,
            egg.ItemId,
            outputItemId,
            planned);
        return true;
    }

    public static bool TryPlace(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item,
        ItemCatalog catalog,
        ushort? preferredEmptySlot,
        out CharacterCreatureInventoryPlacementPlan plan,
        out InventoryPlacementFailure failure)
    {
        plan = null!;
        failure = InventoryPlacementFailure.None;
        if (item.ItemId == 0
            || !catalog.TryGetDefinition(item.ItemId, out var definition)
            || definition.InventoryCategory != ItemInventoryCategory.Creature
            || !CharacterCreatureInventoryLayout.TryGetBagRange(
                definition,
                out var bagStart,
                out var bagEnd))
        {
            failure = InventoryPlacementFailure.UnsupportedCategory;
            return false;
        }

        if (definition.IsStackable && item.CountOrValue == 0)
        {
            failure = InventoryPlacementFailure.InvalidItem;
            return false;
        }

        var planned = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        item = CharacterItemIdentity.Ensure(item, definition);
        var changedSlots = new List<ushort>();
        var candidates = EnumerateCandidateSlots(
            definition,
            preferredEmptySlot,
            bagStart,
            bagEnd).ToArray();

        if (definition.IsStackable)
        {
            var remaining = item.CountOrValue;
            var stackLimit = CharacterInventoryPlanner.GetStackLimit(definition);
            foreach (var existing in planned.Values
                         .Where(candidate => candidate.Slot >= bagStart
                             && candidate.Slot < bagEnd
                             && CanStack(candidate, item)
                             && candidate.CountOrValue < stackLimit)
                         .OrderBy(candidate => candidate.Slot)
                         .ToArray())
            {
                var added = Math.Min(remaining, stackLimit - existing.CountOrValue);
                planned[existing.Slot] = existing with
                {
                    CountOrValue = existing.CountOrValue + added
                };
                changedSlots.Add(existing.Slot);
                remaining -= added;
                if (remaining == 0)
                {
                    break;
                }
            }

            foreach (var slot in candidates)
            {
                if (remaining == 0)
                {
                    break;
                }

                if (planned.ContainsKey(slot))
                {
                    continue;
                }

                var added = Math.Min(remaining, stackLimit);
                planned.Add(
                    slot,
                    item with
                    {
                        Slot = slot,
                        CountOrValue = added,
                        CreatureStomach = null,
                        CreatureStomachRemainderSeconds = null,
                        CreatureExperience = null,
                        CreatureLevel = null,
                        CreatureName = null,
                        CreatureNoCharge = null
                    });
                changedSlots.Add(slot);
                remaining -= added;
            }

            if (remaining != 0 || changedSlots.Count == 0)
            {
                failure = InventoryPlacementFailure.Full;
                return false;
            }
        }
        else
        {
            var destination = candidates.FirstOrDefault(
                slot => !planned.ContainsKey(slot),
                ushort.MaxValue);
            if (destination == ushort.MaxValue)
            {
                failure = InventoryPlacementFailure.Full;
                return false;
            }

            var normalized = CharacterCreatureInventoryLayout.IsCreature(definition)
                ? NormalizeCreatureInstance(item, planned, definition)
                : item with
                {
                    CountOrValue = item.CountOrValue == 0 ? 1u : item.CountOrValue,
                    CreatureStomach = null,
                    CreatureStomachRemainderSeconds = null,
                    CreatureExperience = null,
                    CreatureLevel = null,
                    CreatureName = null,
                    CreatureNoCharge = null
                };
            planned.Add(destination, normalized with { Slot = destination });
            changedSlots.Add(destination);
        }

        plan = new CharacterCreatureInventoryPlacementPlan(
            changedSlots[0],
            planned,
            changedSlots);
        return true;
    }

    public static CharacterCreatureInventoryNormalizationPlan Normalize(
        IEnumerable<CharacterItemRecord> inventory,
        ItemCatalog catalog)
    {
        var source = inventory
            .Where(item => item.ItemId != 0)
            .OrderBy(item => item.Slot)
            .ToArray();
        var normalized = new Dictionary<ushort, CharacterItemRecord>();
        var pending = new List<CharacterItemRecord>();
        var overflow = new List<CharacterMailAttachmentRecord>();

        foreach (var item in source)
        {
            if (!catalog.TryGetDefinition(item.ItemId, out var definition))
            {
                overflow.Add(ToAttachment(item, definition: null));
                continue;
            }

            if (definition.InventoryCategory != ItemInventoryCategory.Creature
                || definition.IsStackable && item.CountOrValue == 0)
            {
                overflow.Add(ToAttachment(item, definition));
                continue;
            }

            var identifiedItem = CharacterItemIdentity.Ensure(item, definition);

            var perSlotLimit = definition.IsStackable
                ? CharacterInventoryPlanner.GetStackLimit(definition)
                : 1u;
            var preservedValue = definition.IsStackable
                ? Math.Min(item.CountOrValue, perSlotLimit)
                : item.CountOrValue;
            if (CharacterCreatureInventoryLayout.IsCompatibleSlot(definition, item.Slot)
                && !normalized.ContainsKey(item.Slot))
            {
                var preserved = identifiedItem with { CountOrValue = preservedValue };
                if (CharacterCreatureInventoryLayout.IsCreature(definition))
                {
                    preserved = NormalizeCreatureInstance(
                        preserved,
                        normalized,
                        definition);
                }
                else if (!definition.IsStackable)
                {
                    preserved = preserved with
                    {
                        CountOrValue = preserved.CountOrValue == 0
                            ? 1u
                            : preserved.CountOrValue,
                        CreatureStomach = null,
                        CreatureStomachRemainderSeconds = null,
                        CreatureExperience = null,
                        CreatureLevel = null,
                        CreatureName = null,
                        CreatureNoCharge = null
                    };
                }

                if (CharacterCreatureInventoryLayout.IsEquippedSlot(item.Slot))
                {
                    preserved = CharacterItemSealing.UnsealWhenEquipped(
                        preserved,
                        definition);
                }

                normalized.Add(item.Slot, preserved);
                if (definition.IsStackable && preservedValue < item.CountOrValue)
                {
                    pending.Add(item with
                    {
                        Slot = ushort.MaxValue,
                        CountOrValue = item.CountOrValue - preservedValue
                    });
                }

                continue;
            }

            pending.Add(identifiedItem with { Slot = ushort.MaxValue });
        }

        foreach (var item in pending)
        {
            if (TryPlace(
                    normalized,
                    item,
                    catalog,
                    preferredEmptySlot: null,
                    out var placement,
                    out _))
            {
                normalized = placement.Inventory;
                continue;
            }

            catalog.TryGetDefinition(item.ItemId, out var definition);
            overflow.Add(ToAttachment(item, definition));
        }

        var result = normalized.Values.OrderBy(item => item.Slot).ToArray();
        var changed = overflow.Count != 0
            || source.Length != result.Length
            || !source.SequenceEqual(result);
        return new CharacterCreatureInventoryNormalizationPlan(
            normalized,
            overflow,
            changed);
    }

    private static IEnumerable<ushort> EnumerateCandidateSlots(
        ItemDefinition definition,
        ushort? preferredSlot,
        ushort bagStart,
        ushort bagEnd)
    {
        if (preferredSlot.HasValue
            && CharacterCreatureInventoryLayout.IsCompatibleSlot(
                definition,
                preferredSlot.Value))
        {
            yield return preferredSlot.Value;
        }

        for (var slot = (int)bagStart; slot < bagEnd; slot++)
        {
            if (slot != preferredSlot)
            {
                yield return checked((ushort)slot);
            }
        }
    }

    private static CharacterItemRecord NormalizeCreatureInstance(
        CharacterItemRecord item,
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        ItemDefinition definition)
    {
        var usedUids = inventory.Values
            .Where(candidate => candidate.CreatureLevel.HasValue)
            .Select(candidate => candidate.CountOrValue)
            .Where(uid => uid != 0)
            .ToHashSet();
        var uid = item.CountOrValue;
        if (uid == 0 || usedUids.Contains(uid))
        {
            uid = AllocateUid(usedUids);
        }

        return item with
        {
            CountOrValue = uid,
            SealState = definition.CreatureSubType == 1
                && definition.AttachType == ItemAttachType.Sealing
                    ? (byte)1
                    : item.SealState,
            Durability = 0,
            // DFLegacy creates a Creature status object for every type-5 entry,
            // including an unhatched egg. Omitting that status record leaves the
            // Creature window with a null lookup result that it dereferences.
            CreatureStomach = item.CreatureStomach ?? 100,
            CreatureStomachRemainderSeconds = (byte)Math.Clamp(
                (int)(item.CreatureStomachRemainderSeconds ?? 0),
                0,
                CreatureHungerPlanner.SecondsPerStomachPoint - 1),
            CreatureExperience = item.CreatureExperience ?? 0,
            CreatureLevel = (byte)Math.Clamp(
                (int)(item.CreatureLevel ?? 1),
                1,
                byte.MaxValue),
            CreatureName = string.IsNullOrWhiteSpace(item.CreatureName)
                ? definition.Name
                : item.CreatureName,
            CreatureNoCharge = item.CreatureNoCharge ?? false
        };
    }

    private static uint AllocateUid(IReadOnlySet<uint> usedUids)
    {
        var uid = usedUids.Count == 0 ? 1u : usedUids.Max() + 1u;
        if (uid == 0)
        {
            uid = 1;
        }

        while (usedUids.Contains(uid))
        {
            uid++;
            if (uid == 0)
            {
                uid = 1;
            }
        }

        return uid;
    }

    private static bool CanStack(
        CharacterItemRecord existing,
        CharacterItemRecord incoming) =>
        existing.ItemId == incoming.ItemId
        && existing.State == incoming.State
        && existing.Durability == incoming.Durability
        && existing.SealState == incoming.SealState;

    private static CharacterMailAttachmentRecord ToAttachment(
        CharacterItemRecord item,
        ItemDefinition? definition) =>
        new(
            item.ItemId,
            definition?.IsStackable == true ? item.CountOrValue : 1,
            item.State,
            item.Durability,
            item.SealState,
            InstanceId: item.InstanceId);
}

public static class CharacterCreatureInventorySorter
{
    private static readonly (ushort Start, ushort End)[] Sections =
    [
        (CharacterCreatureInventoryLayout.CreatureBagStart,
            CharacterCreatureInventoryLayout.CreatureBagEnd),
        (CharacterCreatureInventoryLayout.ArtifactBagStart,
            CharacterCreatureInventoryLayout.ArtifactBagEnd),
        (CharacterCreatureInventoryLayout.StackableBagStart,
            CharacterCreatureInventoryLayout.StackableBagEnd)
    ];

    public static Dictionary<ushort, CharacterItemRecord> Sort(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory)
    {
        var arranged = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var section in Sections)
        {
            var sorted = inventory
                .Where(pair => pair.Key >= section.Start && pair.Key < section.End)
                .Select(pair => (OldSlot: pair.Key, Item: pair.Value))
                .OrderBy(entry => entry.Item.ItemId)
                .ThenBy(entry => entry.Item.CountOrValue)
                .ThenBy(entry => entry.OldSlot)
                .ToArray();
            for (var slot = (int)section.Start; slot < section.End; slot++)
            {
                arranged.Remove(checked((ushort)slot));
            }

            for (var index = 0; index < sorted.Length; index++)
            {
                var destination = checked((ushort)(section.Start + index));
                arranged[destination] = sorted[index].Item with { Slot = destination };
            }
        }

        return arranged;
    }
}
