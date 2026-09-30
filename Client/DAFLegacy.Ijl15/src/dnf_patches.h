#pragma once

#include <cstddef>

#include "ijl15_config.h"
#include "output_paths.h"

namespace dfl::dnf_patches
{
	using dfl::ijl15_config::CeraPurchaseGate;

	// Every feature switch is runtime-configurable through Config.ini beside
	// the client executable: the [Features] section carries one key per switch
	// (named after the Settings fields in ijl15_config.h) and the
	// [CeraPurchaseGate] section carries one key per purchase gate. Missing
	// keys keep the defaults in ijl15_config.cpp, which preserve the behaviour
	// that was hard-coded before the switches became configurable.

	// Purchase-gate policy for the Cera shop patches: the master switch
	// (BypassCeraPurchaseRestrictions) and the per-gate switch must both be
	// enabled. Evaluated while the patch table is built.
	inline bool ShouldBypassCeraPurchaseGate(CeraPurchaseGate gate) noexcept
	{
		const auto& settings = dfl::ijl15_config::Get();
		if (!settings.BypassCeraPurchaseRestrictions)
		{
			return false;
		}
		return settings.BypassCeraPurchaseGates[
			static_cast<std::size_t>(gate)];
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

	bool IsTargetProcess() noexcept;
	bool Apply() noexcept;
}
