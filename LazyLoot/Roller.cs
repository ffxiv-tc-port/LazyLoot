using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Logging;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ECommons.ExcelServices;

namespace LazyLoot;

internal static class Roller
{
    private unsafe delegate bool RollItemRaw(Loot* lootIntPtr, RollResult option, uint lootItemIndex);

    private static RollItemRaw _rollItemRaw;

    private static uint _itemId = 0, _index = 0;

    public static void Clear()
    {
        _itemId = _index = 0;
    }

    public static bool RollOneItem(RollResult option, ref int need, ref int greed, ref int pass)
    {
        if (!GetNextLootItem(out var index, out var loot))
            return false;

        // Make sure we ignore the fulf state if the user has custom item rules
        // Otherwise, we make sure the option is a valid roll
        var userRules = GetPlayerCustomRestrict(loot);
        option = userRules != null
            ? ResultMerge(GetRestrictResult(loot), (RollResult)userRules)
            : ResultMerge(option, GetRestrictResult(loot),
                GetPlayerRestrictByItemId(loot.ItemId, loot.RollState == RollState.UpToNeed));

        if (_itemId == loot.ItemId && index == _index)
        {
            if (LazyLoot.Config.DiagnosticsMode && !LazyLoot.Config.NoPassEmergency)
                DuoLog.Debug(
                    $"{Svc.Data.GetExcelSheet<Item>().GetRow(loot.ItemId).Name.ToString()} has failed to roll for some reason. Passing for safety. [Emergency pass]");

            if (!LazyLoot.Config.NoPassEmergency)
            {
                switch (option)
                {
                    case RollResult.Needed:
                        need--;
                        break;
                    case RollResult.Greeded:
                        greed--;
                        break;
                    default:
                        pass--;
                        break;
                }

                option = RollResult.Passed;
            }
        }

        RollItem(option, index);
        _itemId = loot.ItemId;
        _index = index;

        switch (option)
        {
            case RollResult.Needed:
                need++;
                break;
            case RollResult.Greeded:
                greed++;
                break;
            default:
                pass++;
                break;
        }

        return true;
    }

    private static RollResult GetRestrictResult(LootItem loot)
    {
        var item = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(loot.ItemId);
        if (item == null)
            return RollResult.Passed;

        //Checks what the max possible roll type on the item is
        var stateMax = loot.RollState switch
        {
            RollState.UpToNeed => RollResult.Needed,
            RollState.UpToGreed => RollResult.Greeded,
            _ => RollResult.Passed
        };

        if (item.Value.IsUnique && IsItemUnlocked(loot.ItemId))
            stateMax = RollResult.Passed;

        if (LazyLoot.Config.DiagnosticsMode && stateMax == RollResult.Passed)
            DuoLog.Debug($"{item.Value.Name.ToString()} can only be passed on. [RollState UpToPass]");

        //Checks what the player set loot rules are
        var ruleMax = loot.LootMode switch
        {
            LootMode.Normal => RollResult.Needed,
            LootMode.GreedOnly => RollResult.Greeded,
            _ => RollResult.Passed
        };

        return ResultMerge(stateMax, ruleMax);
    }

    private static unsafe RollResult? GetPlayerCustomRestrict(LootItem loot)
    {
        var lootItem = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(loot.ItemId);
        if (lootItem == null || (lootItem.Value.IsUnique && ItemCount(loot.ItemId) > 0))
            return RollResult.Passed;

        // Here, we will check for the specific rules for items.
        var itemCustomRestriction =
            LazyLoot.Config.Restrictions.Items.FirstOrDefault(x => x.Id == loot.ItemId);
        if (itemCustomRestriction is { Enabled: true })
        {
            if (LazyLoot.Config.DiagnosticsMode)
            {
                var action = itemCustomRestriction.RollRule == RollResult.Passed ? "passing" :
                    itemCustomRestriction.RollRule == RollResult.Greeded ? "greeding" :
                    itemCustomRestriction.RollRule == RollResult.Needed ? "needing" : "passing";
                Svc.Log.Debug($"{lootItem.Value.Name.ToString()} is {action}. [Item Custom Restriction]");
            }

            return itemCustomRestriction.RollRule;
        }

        // Here, we will check for the specific rules for the Duty.
        // Match on the raw id: GetRow(id).RowId is always id when the row exists, and GetRow
        // throws ArgumentOutOfRangeException when it does not, so comparing the id directly is
        // behaviourally identical but cannot throw on an id the current game data has no row for.
        var currentDutyId = GameMain.Instance()->CurrentContentFinderConditionId;
        var dutyCustomRestriction =
            LazyLoot.Config.Restrictions.Duties.FirstOrDefault(x => x.Id == currentDutyId);
        if (dutyCustomRestriction is { Enabled: true })
        {
            if (LazyLoot.Config.DiagnosticsMode)
            {
                var action = dutyCustomRestriction.RollRule == RollResult.Passed ? "passing" :
                    dutyCustomRestriction.RollRule == RollResult.Greeded ? "greeding" :
                    dutyCustomRestriction.RollRule == RollResult.Needed ? "needing" : "passing";
                Svc.Log.Debug(
                    $"{lootItem.Value.Name.ToString()} is {action} due to being in {DutyNameForLog(currentDutyId)}. [Duty Custom Restriction]");
            }

            return dutyCustomRestriction.RollRule;
        }

        return null;
    }

    /// <summary>
    /// Duty name for diagnostic messages only. Never throws: falls back to the raw id when the
    /// current game data has no row for it, so a stale/unknown id degrades the log line instead
    /// of taking down the roll.
    /// </summary>
    private static string DutyNameForLog(uint contentFinderConditionId)
    {
        return Svc.Data.GetExcelSheet<ContentFinderCondition>()
            .TryGetRow(contentFinderConditionId, out var row)
            ? row.Name.ToString()
            : $"#{contentFinderConditionId}";
    }

    private static bool ShouldPassUnlockable(bool restriction, bool onlyUntradeable, Item? item)
    {
        return restriction && (!onlyUntradeable || onlyUntradeable && item!.Value.IsUntradable);
    }

    private static unsafe RollResult GetPlayerRestrictByItemId(uint itemId, bool canNeed)
    {
        var lootItem = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);

        UpdateFadedCopy(itemId, out var orchId);

        if (lootItem == null)
        {
            DuoLog.Debug($"Passing due to unknown item? Please give this ID to the developers: {itemId} [Unknown ID]");
            return RollResult.Passed;
        }

        if (lootItem.Value.IsUnique && ItemCount(itemId) > 0)
        {
            if (LazyLoot.Config.DiagnosticsMode)
                DuoLog.Debug(
                    $"{lootItem.Value.Name} has been passed due to being unique and you already possess one. [Unique Item]");
            return RollResult.Passed;
        }

        // Make sure the item is level 1, with ilv of 1 and is equipment
        if (LazyLoot.Config.NeverPassGlam && lootItem.Value is { LevelEquip: 1, LevelItem.Value.RowId: 1 } &&
            lootItem.Value.EquipSlotCategory.Value.RowId != 0)
        {
            if (LazyLoot.Config.DiagnosticsMode)
                DuoLog.Debug(
                    $"{lootItem.Value.Name} has been set to not pass if possible due to being set to never skip glamour items. [Never Pass Glam]");
            return RollResult.Needed;
        }

        // This checks for faded orchestrion rolls and if their actual orchestrion roll is unlocked, by either all or specific selection
        if (orchId.Count > 0 && orchId.All(IsItemUnlocked))
        {
            if (ShouldPassUnlockable(LazyLoot.Config.RestrictionIgnoreItemUnlocked,
                    LazyLoot.Config.RestrictionAllUnlockablesOnlyUntradeables, lootItem))
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $@"{lootItem.Value.Name} has been passed due to being unlocked and you have ""Pass on all items already unlocked"" enabled. [Pass All Unlocked]");
                return RollResult.Passed;
            }

            if (ShouldPassUnlockable(LazyLoot.Config.RestrictionIgnoreFadedCopy,
                    LazyLoot.Config.RestrictionFadedCopyOnlyUntradeables, lootItem) &&
                lootItem.Value is { FilterGroup: 12, ItemUICategory.RowId: 94 })
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $@"{lootItem.Value.Name} has been passed due to being unlocked and you have ""Pass on unlocked Faded Copies"" enabled. [Pass Faded Copies]");
                return RollResult.Passed;
            }
        }

        if (IsItemUnlocked(itemId))
        {
            if (ShouldPassUnlockable(LazyLoot.Config.RestrictionIgnoreItemUnlocked,
                    LazyLoot.Config.RestrictionAllUnlockablesOnlyUntradeables, lootItem))
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $@"{lootItem.Value.Name} has been passed due to being unlocked and you have ""Pass on all items already unlocked"" enabled. [Pass All Unlocked]");
                return RollResult.Passed;
            }

            if (ShouldPassUnlockable(LazyLoot.Config.RestrictionIgnoreMounts,
                    LazyLoot.Config.RestrictionMountsOnlyUntradeables, lootItem) &&
                lootItem.Value.ItemAction.Value.Type == 1322)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $@"{lootItem.Value.Name} has been passed due to being unlocked and you have ""Pass on unlocked mounts"" enabled. [Pass Unlocked Mounts]");
                return RollResult.Passed;
            }

            if (ShouldPassUnlockable(LazyLoot.Config.RestrictionIgnoreMinions,
                    LazyLoot.Config.RestrictionMinionsOnlyUntradeables, lootItem) &&
                lootItem.Value.ItemAction.Value.Type == 853)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $@"{lootItem.Value.Name} has been passed due to being unlocked and you have ""Pass on unlocked minions"" enabled. [Pass Unlocked Minions]");
                return RollResult.Passed;
            }

            if (ShouldPassUnlockable(LazyLoot.Config.RestrictionIgnoreBardings,
                    LazyLoot.Config.RestrictionBardingsOnlyUntradeables, lootItem) &&
                lootItem.Value.ItemAction.Value.Type == 1013)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $@"{lootItem.Value.Name} has been passed due to being unlocked and you have ""Pass on unlocked bardings"" enabled. [Pass Unlocked Bardings]");
                return RollResult.Passed;
            }

            if (ShouldPassUnlockable(LazyLoot.Config.RestrictionIgnoreEmoteHairstyle,
                    LazyLoot.Config.RestrictionEmoteHairstyleOnlyUntradeables, lootItem) &&
                lootItem.Value.ItemAction.Value.Type == 2633)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $@"{lootItem.Value.Name} has been passed due to being unlocked and you have ""Pass on unlocked emotes and hairstyles"" enabled. [Pass Unlocked Emote/Hairstyle]");
                return RollResult.Passed;
            }

            if (ShouldPassUnlockable(LazyLoot.Config.RestrictionIgnoreTripleTriadCards,
                    LazyLoot.Config.RestrictionTripleTriadCardsOnlyUntradeables, lootItem) &&
                lootItem.Value.ItemAction.Value.Type == 3357)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $@"{lootItem.Value.Name} has been passed due to being unlocked and you have ""Pass on unlocked triple triad cards"" enabled. [Pass Unlocked Cards]");
                return RollResult.Passed;
            }

            if (ShouldPassUnlockable(LazyLoot.Config.RestrictionIgnoreOrchestrionRolls,
                    LazyLoot.Config.RestrictionOrchestrionRollsOnlyUntradeables, lootItem) &&
                lootItem.Value.ItemAction.Value.Type == 25183)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $@"{lootItem.Value.Name} has been passed due to being unlocked and you have ""Pass on unlocked orchestrion rolls"" enabled. [Pass Unlocked Orchestrions]");
                return RollResult.Passed;
            }
        }

        if (LazyLoot.Config.RestrictionSeals)
        {
            if (lootItem.Value is { Rarity: > 1, PriceLow: > 0, ClassJobCategory.RowId: > 0 })
            {
                var gcSealValue = Svc.Data.Excel.GetSheet<GCSupplyDutyReward>()?.GetRow(lootItem.Value.LevelItem.RowId)
                    .SealsExpertDelivery;

                if (gcSealValue < LazyLoot.Config.RestrictionSealsAmnt)
                {
                    if (LazyLoot.Config.DiagnosticsMode)
                        DuoLog.Debug(
                            $@"{lootItem.Value.Name} has been passed due to not reaching your set seals amount (Set: {LazyLoot.Config.RestrictionSealsAmnt} | Item: {gcSealValue} ) [Pass Seals]");
                    return RollResult.Passed;
                }
            }
        }

        if (lootItem.Value.EquipSlotCategory.RowId != 0)
        {
            if (LazyLoot.Config.RestrictionLootLowerThanJobIlvl && canNeed)
            {
                if (lootItem.Value.LevelItem.RowId <
                    Utils.GetPlayerIlevel() - LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold)
                {
                    var toReturn = LazyLoot.Config.RestrictionLootLowerThanJobIlvlRollState == 0
                        ? RollResult.Greeded
                        : RollResult.Passed;
                    if (toReturn == RollResult.Passed && LazyLoot.Config.DiagnosticsMode)
                        DuoLog.Debug(
                            $@"{lootItem.Value.Name} has been passed due to its iLvl being lower than your your current Job (Your: {Utils.GetPlayerIlevel()} | Item: {lootItem.Value.LevelItem.RowId} ) [Pass Item Level Job]");
                    return toReturn;
                }
            }

            if (LazyLoot.Config.RestrictionIgnoreItemLevelBelow
                && lootItem.Value.LevelItem.RowId < LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $@"{lootItem.Value.Name} has been passed due to not reaching the item level required (Set: {LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue} | Item: {lootItem.Value.LevelItem.RowId} ) [Pass Item Level]");
                return RollResult.Passed;
            }

            if (LazyLoot.Config.RestrictionLootIsJobUpgrade && canNeed)
            {
                // seu bloco de verificar equipped continua igual
                var lootItemSlot = lootItem.Value.EquipSlotCategory.RowId;
                var itemsToVerify = new List<uint>();
                var equippedItems =
                    InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);

                for (var i = 0; i < equippedItems->Size; i++)
                {
                    var equippedItem = equippedItems->GetInventorySlot(i);
                    var equippedItemData = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(equippedItem->ItemId);
                    if (equippedItemData == null) continue;
                    if (equippedItemData.Value.EquipSlotCategory.RowId != lootItemSlot) continue;
                    itemsToVerify.Add(equippedItemData.Value.LevelItem.RowId);
                }

                if (itemsToVerify.Count > 0 && itemsToVerify.Min() > lootItem.Value.LevelItem.RowId)
                {
                    var toReturn = LazyLoot.Config.RestrictionLootIsJobUpgradeRollState == 0
                        ? RollResult.Greeded
                        : RollResult.Passed;
                    if (toReturn == RollResult.Passed && LazyLoot.Config.DiagnosticsMode)
                        DuoLog.Debug(
                            $@"{lootItem.Value.Name} has been passed due to its iLvl being lower than your your current equipped item (Item: {lootItem.Value.LevelItem.RowId} | Your: {itemsToVerify.Min()} ) [Pass Item Level Job]");
                    return toReturn;
                }
            }

            if (LazyLoot.Config.RestrictionOtherJobItems && !canNeed)
            {
                DuoLog.Debug(
                    $@"{lootItem.Value.Name} has been passed due to not being an item to your current job ({Player.Object?.ClassJob.Value.Name}) [Pass Not For Job]");
                return RollResult.Passed;
            }
        }

        if (LazyLoot.Config.RestrictionOtherJobItems && lootItem.Value.ItemAction.Value.Type == 29153 &&
            Player.Object?.ClassJob.RowId is not (1 or 19))
        {
            DuoLog.Debug(
                $@"{lootItem.Value.Name} has been passed due to not being an item to your current job and is a GLA/PLD weapon set ({Player.Object?.ClassJob.Value.Name}) [Pass Not For Job]");
            return RollResult.Passed;
        }

        return RollResult.UnAwarded;
    }

    public static void UpdateFadedCopy(uint itemId, out List<uint> orchId)
    {
        orchId = new List<uint>();
        var lumina = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (lumina != null)
            if (lumina.Value.FilterGroup == 12 && lumina.Value.ItemUICategory.RowId == 94)
            {
                var recipe = Svc.Data.GetExcelSheet<Recipe>()
                    ?.Where(x => x.Ingredient.Any(y => y.RowId == lumina.Value.RowId)).Select(x => x.ItemResult.Value)
                    .FirstOrDefault();
                if (recipe != null)
                {
                    if (LazyLoot.Config.DiagnosticsMode)
                        DuoLog.Debug(
                            $"Updating Faded Copy {lumina.Value.Name} ({itemId}) to Non-Faded {recipe.Value.Name} ({recipe.Value.RowId})");
                    orchId.Add(recipe.Value.RowId);
                    return;
                }
            }
    }

    private static RollResult ResultMerge(params RollResult[] results)
    {
        return results.Max() switch
        {
            RollResult.Needed => RollResult.Needed,
            RollResult.Greeded => RollResult.Greeded,
            _ => RollResult.Passed
        };
    }


    private static unsafe bool GetNextLootItem(out uint i, out LootItem loot)
    {
        var span = Loot.Instance()->Items;
        for (i = 0; i < span.Length; i++)
        {
            loot = span[(int)i];

            if (loot.ItemId >= 1000000)
                loot.ItemId -= 1000000;
            if (loot.ChestObjectId is 0 or 0xE0000000)
                continue;
            if (loot.RollResult != RollResult.UnAwarded)
                continue;
            if (loot.RollState is RollState.Rolled or RollState.Unavailable or RollState.Unknown)
                continue;
            if (loot.ItemId == 0)
                continue;
            if (loot.LootMode is LootMode.LootMasterGreedOnly or LootMode.Unavailable)
                continue;

            var checkWeekly = LazyLoot.Config.RestrictionWeeklyLockoutItems;

            var lootId = loot.ItemId;
            // See GetPlayerCustomRestrict: match on the raw id so an id with no row in the
            // current game data cannot throw out of the loot loop.
            var currentDutyId = GameMain.Instance()->CurrentContentFinderConditionId;

            // We load the users restrictions
            var itemCustomRestriction =
                LazyLoot.Config.Restrictions.Items.FirstOrDefault(x =>
                    x.Id == lootId && x is { Enabled: true });
            var dutyCustomRestriction =
                LazyLoot.Config.Restrictions.Duties.FirstOrDefault(x =>
                    x.Id == currentDutyId && x is { Enabled: true, RollRule: RollResult.UnAwarded });

            Item? item = null;

            if (LazyLoot.Config.DiagnosticsMode)
                // Only load the item if diagnostic mode is on
                item = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(loot.ItemId);

            if (itemCustomRestriction != null)
            {
                if (itemCustomRestriction.RollRule == RollResult.UnAwarded)
                {
                    if (LazyLoot.Config.DiagnosticsMode)
                        DuoLog.Debug(
                            $"{item?.Name.ToString()} is being ignored. [Item Custom Restriction]");
                    continue;
                }

                checkWeekly = false;
            }

            if (itemCustomRestriction == null && dutyCustomRestriction != null)
            {
                if (dutyCustomRestriction.RollRule == RollResult.UnAwarded)
                {
                    if (LazyLoot.Config.DiagnosticsMode)
                        DuoLog.Debug(
                            $"{item?.Name.ToString()} is being ignored due to being in {DutyNameForLog(currentDutyId)}. [Duty Custom Restriction]");
                    continue;
                }

                checkWeekly = false;
            }

            // loot.RollValue == 20 means it cant be rolled because one was already obtained this week.
            // we ignore that so it will be passed automatically, as there is nothing the user can do other than
            // pass it
            if (loot.WeeklyLootItem && (byte)loot.RollState != 20 && checkWeekly)
                continue;

            return true;
        }

        loot = default;
        return false;
    }

    private static unsafe void RollItem(RollResult option, uint index)
    {
        try
        {
            // 🔴 原特徵碼 "41 83 F8 ?? 0F 83 ?? ?? ?? ?? 48 89 5C 24 08"(cmp r8d,imm; jae; mov [rsp+8],rbx)在台服 7.20
            //    命中 2 個位址(0x140A3B180 與 0x140A3B4D0)。離線位元組比對證實:這兩支函式除了「相對位移位元組」
            //    (兩個 rip 相對資料參考、三個 call rel32,全部解析到同一組目標)之外**逐位元組相同**——是同一支
            //    RollItemRaw 未被 COMDAT 折疊的兩份複本,呼叫任一者行為完全相同 ⇒ 多重命中良性,不存在「錯命中」。
            //    無法在不使用相對位移位元組(改版易碎)的前提下收斂到 1 命中,且收斂沒有安全效益(兩份相同)。
            //    這裡是 Marshal.GetDelegateForFunctionPointer 的裸函式指標呼叫,故延長特徵碼鎖進 RollItemRaw 專有的
            //    大型堆疊框(sub rsp, 0xf80)以降低「未來改版出現不相干函式意外命中」的機率;仍命中同兩份正解複本。
            //    ScanText 找不到時擲例外→被下方 catch 吞下、_rollItemRaw 留 null→?.Invoke 空操作(fail-closed,擲骰不發生)。
            _rollItemRaw ??=
                Marshal.GetDelegateForFunctionPointer<RollItemRaw>(
                    Svc.SigScanner.ScanText("41 83 F8 ?? 0F 83 ?? ?? ?? ?? 48 89 5C 24 08 48 89 74 24 10 57 48 81 EC 80 0F 00 00"));
            _rollItemRaw?.Invoke(Loot.Instance(), option, index);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Warning at roll");
        }
    }

    public static unsafe int ItemCount(uint itemId)
    {
        return InventoryManager.Instance()->GetInventoryItemCount(itemId);
    }

    public static unsafe bool IsItemUnlocked(uint itemId)
    {
        var exdItem = ExdModule.GetItemRowById(itemId);
        return exdItem is null || UIState.Instance()->IsItemActionUnlocked(exdItem) is 1;
    }

    public static uint ConvertSealsToIlvl(int sealAmnt)
    {
        var sealsSheet = Svc.Data.GetExcelSheet<GCSupplyDutyReward>();
        uint ilvl = 0;
        foreach (var row in sealsSheet)
            if (row.SealsExpertDelivery < sealAmnt)
                ilvl = row.RowId;

        return ilvl;
    }

    // ########################################
    // New Shit to allow IPC call
    // ########################################

    internal enum LlDecision : int
    {
        DoNothing = 0,
        Pass = 1,
        Greed = 2,
        Need = 3
    }

    internal static LlDecision WhatWouldLlDo(uint itemId)
    {
        RollResult baseIntent = LazyLoot.Config.FulfRoll switch
        {
            0 => RollResult.Needed,
            1 => RollResult.Greeded,
            2 => RollResult.Passed,
            _ => RollResult.UnAwarded
        };

        var custom = GetCustomRuleByItemId(itemId);
        var chosen = custom ?? baseIntent;
        var maxAllowed = GetRestrictResultAssumingUpToNeedNormal(itemId);
        var userRestrict = GetPlayerRestrictByItemId(itemId, true);
        var final = MergeWithUnAwarded(chosen, maxAllowed, userRestrict);

        return final switch
        {
            RollResult.UnAwarded => LlDecision.DoNothing,
            RollResult.Passed => LlDecision.Pass,
            RollResult.Greeded => LlDecision.Greed,
            RollResult.Needed => LlDecision.Need,
            _ => LlDecision.Pass
        };
    }

    private static unsafe RollResult? GetCustomRuleByItemId(uint itemId)
    {
        var lootItem = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (lootItem == null || (lootItem.Value.IsUnique && ItemCount(itemId) > 0)) return RollResult.Passed;

        var itemCustom = LazyLoot.Config.Restrictions.Items.FirstOrDefault(x => x.Id == itemId);
        if (itemCustom is { Enabled: true }) return itemCustom.RollRule;

        return null;
    }

    private static RollResult GetRestrictResultAssumingUpToNeedNormal(uint itemId)
    {
        var item = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (item == null) return RollResult.Passed;

        var stateMax = RollResult.Needed;
        const RollResult ruleMax = RollResult.Needed;
        if (item.Value.IsUnique && IsItemUnlocked(itemId)) stateMax = RollResult.Passed;

        return ResultMerge(stateMax, ruleMax);
    }

    private static RollResult MergeWithUnAwarded(params RollResult[] results)
    {
        if (results.All(r => r == RollResult.UnAwarded)) return RollResult.UnAwarded;

        var filtered = results.Where(r => r != RollResult.UnAwarded).ToArray();
        return filtered.Length == 0 ? RollResult.UnAwarded : ResultMerge(filtered);
    }
}