#pragma once

#include <cstdint>

#include "output_paths.h"

namespace dfl::dnf_patches
{
	inline constexpr bool DisableDebuggerDetection = true;
	inline constexpr bool AllowMultipleClientInstances = true;
	inline constexpr bool DisableStartupMinimize = true;
	inline constexpr bool SinglePermanentAvatarPeriodOption = true;
	inline constexpr bool EnableCustomStackableUseMode1 = true;
	inline constexpr bool RemoveCustomPopupAnnouncementTitle = true;
	inline constexpr bool UseExternalDnfStringTable = true;
	inline constexpr bool FixMailboxArchiveRefresh = true;
	inline constexpr bool BypassCeraPurchaseRestrictions = true;
	// Skip the client's rotate/xor calls at the confirmed log writers so the
	// DNF.trc and NeopleEngine streams remain plain text without changing
	// unrelated string helpers.
	inline constexpr bool ForcePlainTextLogs = true;

	enum class CeraPurchaseGate
	{
		LegacySaleWindow,
		PerCharacterLimit,
		CharacterLevel,
		ClientInventoryCapacity,
		JobCompatibility,
		CurrencyBalance,
		RequiredOption,
		GiftEligibility,
	};

	constexpr bool ShouldBypassCeraPurchaseGate(
		CeraPurchaseGate gate) noexcept
	{
		if (!BypassCeraPurchaseRestrictions)
		{
			return false;
		}

		switch (gate)
		{
		case CeraPurchaseGate::LegacySaleWindow:
		case CeraPurchaseGate::PerCharacterLimit:
		case CeraPurchaseGate::CharacterLevel:
			return true;

		case CeraPurchaseGate::ClientInventoryCapacity:
		case CeraPurchaseGate::JobCompatibility:
		case CeraPurchaseGate::CurrencyBalance:
		case CeraPurchaseGate::RequiredOption:
		case CeraPurchaseGate::GiftEligibility:
			return false;
		}

		return false;
	}

	// Add [etc] stackable item IDs here. Listed items use the client's mode-1
	// path and therefore send command 47. Rebuild IJL15 after every change.
	inline constexpr std::uint32_t EtcStackableUseMode1ItemIds[] = {
		7518,
		7519,
		1121,
		1122,
		1123,
	};

	constexpr bool IsEtcStackableUseMode1ItemId(
		std::uint32_t itemId) noexcept
	{
		for (const std::uint32_t configuredItemId
			: EtcStackableUseMode1ItemIds)
		{
			if (configuredItemId == itemId)
			{
				return true;
			}
		}
		return false;
	}

	inline constexpr std::uint32_t CeraBoosterStackableType = 13;

	constexpr bool ShouldUseStackableMode1(
		std::uint32_t itemId,
		std::uint32_t stackableType) noexcept
	{
		return stackableType == CeraBoosterStackableType
			|| stackableType == 10
				&& IsEtcStackableUseMode1ItemId(itemId);
	}

    // The FPS/memory overlay and mouse-wheel hook are runtime options in the
    // Config.ini file beside DNF.exe. Their defaults are enabled there.
	// Keep Type16Brown text but suppress the client's CHAT_MESSAGE notification
	// sound. Type4Brown and other message types retain their normal behavior.
	inline constexpr bool DisableType16ChatMessageSound = true;

	bool IsTargetProcess() noexcept;
	bool Apply() noexcept;
}
