#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

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

	namespace detail
	{
		struct RepairAllScopePatch
		{
			std::uintptr_t Address;
			std::uintptr_t ResumeAddress;
			std::array<unsigned char, 6> Expected;
			std::array<unsigned char, 6> Replacement;
		};

		// All-repair (slot 0xFFFF) walks worn equipment and the main bag in
		// separate loops. Both patches jump over the main-bag half so pricing,
		// the confirm dialog and the REPLY25 durability fill cover only the
		// range the server charges.
		inline constexpr std::array<RepairAllScopePatch, 2> RepairAllScopePatches{{
			{
				// sub_7CDC10: return the worn-only price/count before scanning
				// the main bag. Both affordability and the confirm dialog use it.
				0x007CDC83,
				0x007CDEA9,
				{0x8B, 0x35, 0xEC, 0xDC, 0xB5, 0x00},
				{0xE9, 0x21, 0x02, 0x00, 0x00, 0x90}
			},
			{
				// REPLY25's 0xFFFF branch: keep the worn loop, skip the main-bag
				// durability fill, then retain window cleanup and ITEM_REPAIR.
				0x00411C79,
				0x00411D6B,
				{0x8B, 0x0D, 0xEC, 0xDC, 0xB5, 0x00},
				{0xE9, 0xED, 0x00, 0x00, 0x00, 0x90}
			}
		}};
	}

	bool IsTargetProcess() noexcept;
	bool Apply() noexcept;
}
