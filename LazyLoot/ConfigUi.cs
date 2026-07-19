using ImGuiNET;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using PunishLib.ImGuiMethods;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ECommons.Reflection;

namespace LazyLoot;

public class ConfigUi : Window, IDisposable
{
    private static List<CustomRestriction> _importedRestrictions;
    private static int _debugValue;
    private readonly WindowSystem _windowSystem = new();

    [StructLayout(LayoutKind.Explicit, Size = 0x40)]
    private struct DebugLootItem
    {
        [FieldOffset(0x00)] public uint ChestObjectId;
        [FieldOffset(0x04)] public uint ChestItemIndex; // This loot item's index in the chest it came from
        [FieldOffset(0x08)] public uint ItemId;
        [FieldOffset(0x0C)] public ushort ItemCount;

        [FieldOffset(0x1C)] public uint GlamourItemId;
        [FieldOffset(0x20)] public RollState RollState;
        [FieldOffset(0x24)] public RollResult RollResult;
        [FieldOffset(0x28)] public byte RollValue;
        [FieldOffset(0x34)] public byte Unk1;
        [FieldOffset(0x38)] public byte Unk2;
        [FieldOffset(0x2C)] public float Time;
        [FieldOffset(0x30)] public float MaxTime;

        [FieldOffset(0x38)] public LootMode LootMode;
    }

    public ConfigUi() : base("Lazy Loot 設定")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 200),
            MaximumSize = new Vector2(99999, 99999)
        };
        _windowSystem.AddWindow(this);
        Svc.PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
    }

    public void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        GC.SuppressFinalize(this);
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("config"))
        {
            if (ImGui.BeginTabItem("功能"))
            {
                DrawFeatures();
                ImGui.Separator();
                DrawRollingDelay();
                ImGui.Separator();
                DrawChatAndToast();
                ImGui.Separator();
                DrawDtrToggle();
                ImGui.Separator();
                DrawFulf();
                ImGui.Separator();
                DrawDiagnostics();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("使用者限制"))
            {
                DrawUserRestriction();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("About"))
            {
                AboutTab.Draw("LazyLoot");
                ImGui.EndTabItem();
            }

#if DEBUG
            if (ImGui.BeginTabItem("Debug"))
            {
                DrawDebug();
                ImGui.EndTabItem();
            }
#endif

            ImGui.EndTabBar();
        }
    }

    private static IDalamudTextureWrap? GetItemIcon(uint id)
    {
        return Svc.Texture.GetFromGameIcon(new GameIconLookup
        {
            IconId = id
        }).GetWrapOrDefault();
    }

    private unsafe void DrawDebug()
    {
        if (ImGui.CollapsingHeader("Is Item Unlocked?"))
        {
            ImGui.InputInt("Debug Value Tester", ref _debugValue);
            ImGui.Text($"Is Unlocked: {Roller.IsItemUnlocked((uint)_debugValue)}");
        }

        if (ImGui.CollapsingHeader("Loot"))
        {
            var loot = Loot.Instance();
            if (loot != null)
            {
                foreach (var item in loot->Items)
                {
                    if (item.ItemId == 0) continue;
                    var casted = (DebugLootItem*)&item;
                    ImGui.PushID($"{casted->ItemId}");
                    Dalamud.Utility.Util.ShowStruct(casted);
                }
            }
        }

        // This is here in case we ever need to debug faded copies again.
        // Please do not delete <3

        //if (ImGui.Button("Faded Copy Converter Check?"))
        //{
        //    Roller.UpdateFadedCopy((uint)debugValue, out uint nonfaded);
        //    Svc.Log.Debug($"Non-Faded is {nonfaded}");\
        //}

        //if (ImGui.Button("Check all Faded Copies"))
        //{
        //    foreach (var i in Svc.Data.GetExcelSheet<Item>().Where(x => x.FilterGroup == 12 && x.ItemUICategory.Row == 94))
        //    {
        //        Roller.UpdateFadedCopy((uint)i.RowId, out uint nonfaded);
        //        Svc.Log.Debug($"{i.Name}");
        //    }
        //}
    }

    private void DrawDiagnostics()
    {
        ImGuiEx.LineCentered("DiagnosticsLabel", () => ImGuiEx.TextUnderlined("診斷與疑難排解"));

        if (ImGui.Checkbox("診斷模式", ref LazyLoot.Config.DiagnosticsMode))
            LazyLoot.Config.Save();

        ImGuiComponents.HelpMarker(
            "每當道具被放棄時，會在聊天欄輸出附帶原因的額外訊息。這有助於向開發者反映問題，或了解 LazyLoot 為何決定放棄該道具。\r\n\r\n這些訊息只有你自己看得到，其他玩家無法看見。");

        if (ImGui.Checkbox("擲骰失敗時不要放棄道具。", ref LazyLoot.Config.NoPassEmergency))
            LazyLoot.Config.Save();

        ImGuiComponents.HelpMarker(
            "正常情況下 LazyLoot 會在擲骰失敗時放棄該道具。啟用此選項可防止在這種情況下放棄道具。請注意這樣做可能會有異常的副作用，僅建議在遇到緊急放棄問題時才使用。");
    }

    public override void OnClose()
    {
        LazyLoot.Config.Save();
        base.OnClose();
    }

    private static void DrawFeatures()
    {
        ImGuiEx.LineCentered("FeaturesLabel", () => ImGuiEx.TextUnderlined("LazyLoot 擲骰指令"));
        ImGui.Columns(2, null, false);
        ImGui.SetColumnWidth(0, 80);
        ImGui.Text("/lazy need");
        ImGui.NextColumn();
        ImGui.Text("對所有道具擲需要。若無法擲需要，則擲貪要（若也無法擲貪要則放棄）。");
        ImGui.NextColumn();
        ImGui.Text("/lazy greed");
        ImGui.NextColumn();
        ImGui.Text("對所有道具擲貪要。若無法擲貪要，則放棄。");
        ImGui.NextColumn();
        ImGui.Text("/lazy pass");
        ImGui.NextColumn();
        ImGui.Text("放棄尚未擲骰的道具。");
        ImGui.NextColumn();
        ImGui.Columns(1);
    }

    private static void DrawRollingDelay()
    {
        ImGuiEx.LineCentered("RollingDelayLabel", () => ImGuiEx.TextUnderlined("擲骰指令延遲"));
        ImGui.SetNextItemWidth(100);

        if (ImGui.DragFloatRange2("道具間的擲骰延遲", ref LazyLoot.Config.MinRollDelayInSeconds,
                ref LazyLoot.Config.MaxRollDelayInSeconds, 0.1f))
        {
            LazyLoot.Config.MinRollDelayInSeconds = Math.Max(LazyLoot.Config.MinRollDelayInSeconds, 0.5f);

            LazyLoot.Config.MaxRollDelayInSeconds = Math.Max(LazyLoot.Config.MaxRollDelayInSeconds,
                LazyLoot.Config.MinRollDelayInSeconds + 0.1f);
        }
    }

    private static void DrawOnlyUntradeableCheckbox(string id, ref bool parentRestriction, ref bool thisRestriction)
    {
        if (!parentRestriction) return;
        ImGui.PushID(id);
        ImGui.Indent(20f);
        ImGui.Checkbox(
            "僅限不可交易物品",
            ref thisRestriction);
        ImGui.Unindent(20f);
        ImGui.PopID();
    }

    private static void DrawUserRestrictionEverywhere()
    {
        ImGui.TextWrapped("\u6b64\u9801\u9762\u7684\u8a2d\u5b9a\u9069\u7528\u65bc\u6240\u6709\u9053\u5177\uff0c\u7121\u8ad6\u662f\u5426\u53ef\u4ea4\u6613\u3002");
        ImGui.Separator();
        ImGui.Checkbox("\u653e\u68c4\u88dd\u5099\u7b49\u7d1a\u4f4e\u65bc\u4ee5\u4e0b\u6578\u503c\u7684\u9053\u5177",
            ref LazyLoot.Config.RestrictionIgnoreItemLevelBelow);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(50);
        ImGui.DragInt("###RestrictionIgnoreItemLevelBelowValue",
            ref LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue);
        if (LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue < 0)
            LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue = 0;

        Utils.CheckboxTextWrapped(
            "\u653e\u68c4\u6240\u6709\u5df2\u89e3\u9396\u7684\u9053\u5177\u3002\uff08\u4e09\u5f35\u724c\u5361\u7247\u3001\u6f14\u594f\u6703\u6a02\u8b5c\u3001\u892a\u8272\u8907\u88fd\u54c1\u3001\u5bf5\u7269\u3001\u5750\u9a0e\u3001\u8868\u60c5\u52d5\u4f5c\u3001\u9aee\u578b\uff09",
            ref LazyLoot.Config.RestrictionIgnoreItemUnlocked);

        if (!LazyLoot.Config.RestrictionIgnoreItemUnlocked)
        {
            ImGui.Checkbox("\u653e\u68c4\u5df2\u89e3\u9396\u7684\u5750\u9a0e\u3002", ref LazyLoot.Config.RestrictionIgnoreMounts);
            DrawOnlyUntradeableCheckbox(
                "RestrictionMountsOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreMounts,
                ref LazyLoot.Config.RestrictionMountsOnlyUntradeables
            );

            ImGui.Checkbox("\u653e\u68c4\u5df2\u89e3\u9396\u7684\u5bf5\u7269\u3002", ref LazyLoot.Config.RestrictionIgnoreMinions);
            DrawOnlyUntradeableCheckbox(
                "RestrictionMinionsOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreMinions,
                ref LazyLoot.Config.RestrictionMinionsOnlyUntradeables
            );

            ImGui.Checkbox("\u653e\u68c4\u5df2\u89e3\u9396\u7684\u5750\u9a0e\u88dd\u7532\u3002", ref LazyLoot.Config.RestrictionIgnoreBardings);
            DrawOnlyUntradeableCheckbox(
                "RestrictionBardingsOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreBardings,
                ref LazyLoot.Config.RestrictionBardingsOnlyUntradeables
            );

            ImGui.Checkbox("\u653e\u68c4\u5df2\u89e3\u9396\u7684\u4e09\u5f35\u724c\u5361\u7247\u3002",
                ref LazyLoot.Config.RestrictionIgnoreTripleTriadCards);
            DrawOnlyUntradeableCheckbox(
                "RestrictionTripleTriadCardsOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreTripleTriadCards,
                ref LazyLoot.Config.RestrictionTripleTriadCardsOnlyUntradeables
            );

            ImGui.Checkbox("\u653e\u68c4\u5df2\u89e3\u9396\u7684\u8868\u60c5\u52d5\u4f5c\u8207\u9aee\u578b\u3002",
                ref LazyLoot.Config.RestrictionIgnoreEmoteHairstyle);
            DrawOnlyUntradeableCheckbox(
                "RestrictionEmoteHairstyleOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreEmoteHairstyle,
                ref LazyLoot.Config.RestrictionEmoteHairstyleOnlyUntradeables
            );

            ImGui.Checkbox("\u653e\u68c4\u5df2\u89e3\u9396\u7684\u6f14\u594f\u6703\u6a02\u8b5c\u3002",
                ref LazyLoot.Config.RestrictionIgnoreOrchestrionRolls);
            DrawOnlyUntradeableCheckbox(
                "RestrictionOrchestrionRollsOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreOrchestrionRolls,
                ref LazyLoot.Config.RestrictionOrchestrionRollsOnlyUntradeables
            );

            ImGui.Checkbox("\u653e\u68c4\u5df2\u89e3\u9396\u7684\u892a\u8272\u8907\u88fd\u54c1\u3002", ref LazyLoot.Config.RestrictionIgnoreFadedCopy);
            DrawOnlyUntradeableCheckbox(
                "RestrictionFadedCopyOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreFadedCopy,
                ref LazyLoot.Config.RestrictionFadedCopyOnlyUntradeables
            );
        }

        DrawOnlyUntradeableCheckbox(
            "RestrictionAllUnlockablesOnlyUntradeables",
            ref LazyLoot.Config.RestrictionIgnoreItemUnlocked,
            ref LazyLoot.Config.RestrictionAllUnlockablesOnlyUntradeables
        );

        ImGui.Checkbox("\u653e\u68c4\u76ee\u524d\u8077\u696d\u7121\u6cd5\u4f7f\u7528\u7684\u9053\u5177\u3002",
            ref LazyLoot.Config.RestrictionOtherJobItems);

        ImGui.Checkbox("\u4e0d\u5c0d\u6709\u6bcf\u9031\u6b21\u6578\u9650\u5236\u7684\u9053\u5177\u6216\u4efb\u52d9\u64f2\u9ab0\u3002",
            ref LazyLoot.Config.RestrictionWeeklyLockoutItems);

        ImGui.Checkbox("###RestrictionWeeklyLockoutItems", ref LazyLoot.Config.RestrictionLootLowerThanJobIlvl);
        ImGui.SameLine();
        ImGui.Text("\u64f2");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("###RestrictionLootLowerThanJobIlvlRollState",
            ref LazyLoot.Config.RestrictionLootLowerThanJobIlvlRollState, new[] { "\u8caa\u8981", "\u653e\u68c4" }, 2);
        ImGui.SameLine();
        ImGui.Text("\u88dd\u5099\u7b49\u7d1a\u4f4e\u65bc");
        ImGui.SetNextItemWidth(50);
        ImGui.SameLine();
        ImGui.DragInt("###RestrictionLootLowerThanJobIlvlTreshold",
            ref LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold);
        if (LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold < 0)
            LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold = 0;
        ImGui.SameLine();
        ImGui.Text($"\uff08\u4f4e\u65bc\u76ee\u524d\u8077\u696d\u88dd\u5099\u7b49\u7d1a \u2605 {Utils.GetPlayerIlevel()}\uff09\u7684\u9053\u5177\u3002");
        ImGuiComponents.HelpMarker("\u6b64\u8a2d\u5b9a\u50c5\u9069\u7528\u65bc\u53ef\u4ee5\u64f2\u9700\u8981\u7684\u88dd\u5099\u3002");

        ImGui.Checkbox("###RestrictionLootIsJobUpgrade", ref LazyLoot.Config.RestrictionLootIsJobUpgrade);
        ImGui.SameLine();
        ImGui.Text("\u64f2");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("###RestrictionLootIsJobUpgradeRollState",
            ref LazyLoot.Config.RestrictionLootIsJobUpgradeRollState,
            new[] { "\u8caa\u8981", "\u653e\u68c4" }, 2);
        ImGui.SameLine();
        ImGui.Text("\u76ee\u524d\u88dd\u5099\u7684\u540c\u985e\u578b\u9053\u5177\u88dd\u5099\u7b49\u7d1a\u8f03\u9ad8\u6642\u7684\u9053\u5177\u3002");
        ImGuiComponents.HelpMarker("\u6b64\u8a2d\u5b9a\u50c5\u9069\u7528\u65bc\u53ef\u4ee5\u64f2\u9700\u8981\u7684\u88dd\u5099\u3002");

        ImGui.Checkbox("###RestrictionSeals", ref LazyLoot.Config.RestrictionSeals);
        ImGui.SameLine();
        ImGui.Text("\u653e\u68c4\u5c08\u5bb6\u9001\u8ca8\u9ede\u6578\u4f4e\u65bc\u4ee5\u4e0b\u6578\u503c\u7684\u9053\u5177");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.DragInt("###RestrictionSealsAmnt", ref LazyLoot.Config.RestrictionSealsAmnt);
        ImGui.SameLine();
        ImGui.Text($"\uff08\u88dd\u5099\u7b49\u7d1a {Roller.ConvertSealsToIlvl(LazyLoot.Config.RestrictionSealsAmnt)} \u53ca\u4ee5\u4e0b\uff09");
        ImGuiComponents.HelpMarker(
            "\u6b64\u8a2d\u5b9a\u50c5\u9069\u7528\u65bc\u53ef\u7528\u65bc\u5c08\u5bb6\u9001\u8ca8\u7684\u88dd\u5099\u3002");

        ImGui.Checkbox("###NeverPassGlam", ref LazyLoot.Config.NeverPassGlam);
        ImGui.SameLine();
        ImGui.TextWrapped("\u7d55\u4e0d\u653e\u68c4\u5e7b\u60f3\u9053\u5177\uff08\u88dd\u5099\u7b49\u7d1a\u8207\u54c1\u7d1a\u5747\u70ba1\u7684\u9053\u5177\uff09");
    }

    private static void CenterText()
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetColumnWidth() - ImGui.GetFrameHeight()) * 0.5f);
    }

    private static nint GetDutyIcon(ContentFinderCondition duty)
    {
        var icon = duty is { HighEndDuty: true, ContentType.Value.RowId: 5 }
            ? Svc.Data.GetExcelSheet<ContentType>()
                .FirstOrDefault(x => x.RowId == 28).Icon
            : duty.ContentType.Value.Icon;
        if (icon == 0)
        {
            return 0;
        }

        var itemIcon = GetItemIcon(icon);
        return itemIcon?.ImGuiHandle ?? default;
    }

    private static void DrawUserRestrictionItems()
    {
        ImGui.Dummy(new Vector2(0, 6));
        ImGuiEx.LineCentered("ItemRestrictionWarning",
            () =>
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                ImGui.TextWrapped("這些規則會覆蓋除每週次數限制以外的所有其他限制設定。");
                ImGui.PopStyleColor();
            });
        ImGui.Dummy(new Vector2(0, 6));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0, 6));

        var items = LazyLoot.Config.Restrictions.Items;

        if (items.Count == 0)
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));
            if (ImGui.BeginChild("##UserRestrictionEmptyState", new Vector2(-1, 60), true,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGuiEx.TextCentered("尚未新增任何道具。");
                ImGuiEx.TextCentered("點擊下方的「新增道具」開始新增。");
                ImGui.EndChild();
            }

            ImGui.PopStyleVar();
            ImGui.Dummy(new Vector2(0, 6));
        }
        else
        {
            if (ImGui.BeginTable("UserRestrictionItemsTable", 8, ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("啟用", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("圖示", ImGuiTableColumnFlags.WidthFixed, 32f);
                ImGui.TableSetupColumn("名稱", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("需要", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("貪要", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("放棄", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("不處理", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableHeadersRow();

                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var restrictedItem = Svc.Data.GetExcelSheet<Item>().GetRow(item.Id);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var enabled = item.Enabled;
                    CenterText();
                    if (ImGui.Checkbox($"##{item.Id}", ref enabled))
                    {
                        item.Enabled = enabled;
                        LazyLoot.Config.Save();
                    }

                    ImGui.TableNextColumn();
                    CenterText();

                    var icon = GetItemIcon(restrictedItem.Icon);
                    if (icon != null)
                        ImGui.Image(icon.ImGuiHandle, new Vector2(24, 24));
                    else
                        ImGui.Text("-");

                    ImGui.TableNextColumn();
                    ImGui.Text(restrictedItem.Name.ToString());
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(restrictedItem.Name.ToString());

                    ImGui.TableNextColumn();
                    CenterText();
                    if (ImGui.RadioButton($"##need{item.Id}", item.RollRule == RollResult.Needed))
                    {
                        item.RollRule = RollResult.Needed;
                        LazyLoot.Config.Save();
                    }

                    ImGui.TableNextColumn();
                    CenterText();
                    if (ImGui.RadioButton($"##greed{item.Id}", item.RollRule == RollResult.Greeded))
                    {
                        item.RollRule = RollResult.Greeded;
                        LazyLoot.Config.Save();
                    }

                    ImGui.TableNextColumn();
                    CenterText();
                    if (ImGui.RadioButton($"##pass{item.Id}", item.RollRule == RollResult.Passed))
                    {
                        item.RollRule = RollResult.Passed;
                        LazyLoot.Config.Save();
                    }

                    ImGui.TableNextColumn();
                    CenterText();
                    if (ImGui.RadioButton($"##doNothing{item.Id}", item.RollRule == RollResult.UnAwarded))
                    {
                        item.RollRule = RollResult.UnAwarded;
                        LazyLoot.Config.Save();
                    }

                    ImGui.TableNextColumn();
                    if (ImGui.Button($"移除##{item.Id}"))
                    {
                        LazyLoot.Config.Restrictions.Items.RemoveAt(i);
                        LazyLoot.Config.Save();
                        break;
                    }
                }

                ImGui.EndTable();
            }
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 0));
        if (ImGui.Button("匯出", new Vector2(60, 0)))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(LazyLoot.Config.Restrictions.Items);
            ImGui.SetClipboardText(json);
            Notify.Success("道具限制設定已複製到剪貼簿！");
        }

        ImGui.SameLine();
        if (ImGui.Button("匯入", new Vector2(60, 0)))
        {
            try
            {
                bool userImported = ImportFromClipboard("import_item_confirmation");
                if (!userImported)
                {
                    Notify.Error("匯入道具限制設定失敗 - 格式無效");
                }
            }
            catch (Exception e)
            {
                e.Log();
            }
        }

        ImGui.SameLine();

        var itemSheet = Svc.Data.GetExcelSheet<Item>();
        Utils.PopupListButton(
            buttonLabel: "新增道具...",
            popupId: "item_search_add",
            popupTitle: "搜尋道具：",
            getResults: q =>
            {
                if (uint.TryParse(q, out var searchId))
                {
                    return itemSheet
                        .Where(x => x.RowId == searchId
                                    && x is { RowId: > 0, Name.IsEmpty: false }
                                    && LazyLoot.Config.Restrictions.Items.All(d => d.Id != x.RowId));
                }

                return itemSheet
                    .Where(x =>
                        x.Name.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)
                        && x is { RowId: > 0, Name.IsEmpty: false }
                        && LazyLoot.Config.Restrictions.Items.All(d => d.Id != x.RowId));
            },
            getItemLabel: item => $" {item.Name} (ID: {item.RowId})",
            renderItem: item =>
            {
                var icon = GetItemIcon(item.Icon);
                if (icon != null)
                {
                    ImGui.Image(icon.ImGuiHandle, new Vector2(16, 16));
                    ImGui.SameLine();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(item.Name.ToString());
                ImGui.SameLine();
            },
            onSelect: duty =>
            {
                LazyLoot.Config.Restrictions.Items.Add(new CustomRestriction
                {
                    Id = duty.RowId,
                    Enabled = true,
                    RollRule = RollResult.UnAwarded
                });
                LazyLoot.Config.Save();
            }
        );

        ImGui.PopStyleVar();
        

        if (ImGui.BeginPopup("import_item_confirmation", ImGuiWindowFlags.AlwaysAutoResize))
        {
            bool validation = ValidateImport(itemSheet);
            if (!validation)
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.Text("確定要取代目前的道具限制設定嗎？");
            ImGuiEx.LineCentered(() => ImGuiEx.TextUnderlined("此操作無法復原。"));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(40 / 255f, 167 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("是", new Vector2(100f, 0)))
            {
                if (_importedRestrictions != null)
                {
                    LazyLoot.Config.Restrictions.Items = _importedRestrictions;
                    LazyLoot.Config.Save();
                    Notify.Success("已成功匯入道具限制設定！");
                }

                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(220 / 255f, 53 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("否", new Vector2(-1, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }
    }

    private static bool ValidateImport<T>(ExcelSheet<T> sheet) where T : struct, IExcelRow<T>
    {
        bool bail = false;
        foreach (var item in _importedRestrictions)
        {
            if (sheet.Any(x => x.RowId == item.Id)) continue;
            bail = true;
            Notify.Error($"匯入的限制設定包含無效的道具 ID：{item.Id}。已取消匯入。");
        }

        if (bail)
        {
            ImGui.CloseCurrentPopup();
            return false;
        }

        return true;
    }

    private static bool ImportFromClipboard(string popupname)
    {
        var clipboardText = ImGui.GetClipboardText();
        if (string.IsNullOrEmpty(clipboardText))
        {
            Notify.Error("剪貼簿中沒有可匯入的內容");
            return false;
        }

        try
        {
            var result = JsonSerializer.Deserialize<List<CustomRestriction>>(clipboardText);
            if (result != null)
            {
                _importedRestrictions = result;
                ImGui.OpenPopup(popupname);
            }
        }
        catch (Exception e)
        {
            e.Log();
            return false;
        }

        return true;
    }

    private static void DrawUserRestrictionDuties()
    {
        
        ImGui.Dummy(new Vector2(0, 6));
        ImGuiEx.LineCentered("DutyRestrictionWarning",
            () =>
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                ImGui.TextWrapped("這些規則會覆蓋主要限制設定，但若與道具限制設定衝突，則以道具限制設定為準。");
                ImGui.PopStyleColor();
            });
        ImGui.Dummy(new Vector2(0, 6));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0, 6));

        var duties = LazyLoot.Config.Restrictions.Duties;

        if (duties.Count == 0)
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));
            if (ImGui.BeginChild("##UserRestrictionDutyEmptyState", new Vector2(-1, 60), true,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGuiEx.TextCentered("尚未新增任何任務。");
                ImGuiEx.TextCentered("點擊下方的「新增任務」開始新增。");
                ImGui.EndChild();
            }

            ImGui.PopStyleVar();
            ImGui.Dummy(new Vector2(0, 6));
        }
        else
        {
            if (ImGui.BeginTable("UserRestrictionDutiesTable", 8, ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("啟用", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("類型", ImGuiTableColumnFlags.WidthFixed, 32f);
                ImGui.TableSetupColumn("名稱", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("需要", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("貪要", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("放棄", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("不處理", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableHeadersRow();

                for (var i = 0; i < duties.Count; i++)
                {
                    var duty = duties[i];
                    var restrictedDuty = Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRow(duty.Id);
                    var enabled = duty.Enabled;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    CenterText();
                    if (ImGui.Checkbox($"##{duty.Id}", ref enabled))
                    {
                        duty.Enabled = enabled;
                        LazyLoot.Config.Save();
                    }

                    ImGui.TableNextColumn();
                    CenterText();

                    ImGui.Image(GetDutyIcon(restrictedDuty), new Vector2(24, 24));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip((restrictedDuty is { HighEndDuty: true, ContentType.Value.RowId: 5 }
                            ? Svc.Data.GetExcelSheet<ContentType>()
                                .FirstOrDefault(x => x.RowId == 28).Name
                            : restrictedDuty.ContentType.Value.Name).ToString());

                    ImGui.TableNextColumn();
                    ImGui.Text(restrictedDuty.Name.ToString());
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(restrictedDuty.Name.ToString());

                    ImGui.TableNextColumn();
                    CenterText();
                    if (ImGui.RadioButton($"##need{duty.Id}", duty.RollRule == RollResult.Needed))
                    {
                        duty.RollRule = RollResult.Needed;
                        LazyLoot.Config.Save();
                    }

                    ImGui.TableNextColumn();
                    CenterText();
                    if (ImGui.RadioButton($"##greed{duty.Id}", duty.RollRule == RollResult.Greeded))
                    {
                        duty.RollRule = RollResult.Greeded;
                        LazyLoot.Config.Save();
                    }

                    ImGui.TableNextColumn();
                    CenterText();
                    if (ImGui.RadioButton($"##pass{duty.Id}", duty.RollRule == RollResult.Passed))
                    {
                        duty.RollRule = RollResult.Passed;
                        LazyLoot.Config.Save();
                    }

                    ImGui.TableNextColumn();
                    CenterText();
                    if (ImGui.RadioButton($"##doNothing{duty.Id}", duty.RollRule == RollResult.UnAwarded))
                    {
                        duty.RollRule = RollResult.UnAwarded;
                        LazyLoot.Config.Save();
                    }

                    ImGui.TableNextColumn();
                    if (ImGui.Button($"移除##{duty.Id}"))
                    {
                        LazyLoot.Config.Restrictions.Duties.RemoveAt(i);
                        LazyLoot.Config.Save();
                        break;
                    }
                }

                ImGui.EndTable();
            }
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 0));
        if (ImGui.Button("匯出", new Vector2(60, 0)))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(LazyLoot.Config.Restrictions.Duties);
            ImGui.SetClipboardText(json);
            Notify.Success("任務限制設定已複製到剪貼簿！");
        }

        ImGui.SameLine();
        if (ImGui.Button("匯入", new Vector2(60, 0)))
        {
            try
            {
                bool userImported = ImportFromClipboard("import_duty_confirmation");
                if (!userImported)
                {
                    Notify.Error("匯入任務限制設定失敗 - 格式無效");
                }
            }
            catch (Exception e)
            {
                e.Log();
            }
        }

        ImGui.SameLine();
        var dutySheet = Svc.Data.GetExcelSheet<ContentFinderCondition>();
        Utils.PopupListButton(
            buttonLabel: "新增任務...",
            popupId: "duty_search_add",
            popupTitle: "搜尋任務：",
            getResults: q =>
            {
                if (uint.TryParse(q, out var searchId))
                {
                    return dutySheet
                        .Where(x => x.RowId == searchId
                                    && x is { RowId: > 0, Name.IsEmpty: false }
                                    && LazyLoot.Config.Restrictions.Duties.All(d => d.Id != x.RowId));
                }

                return dutySheet
                    .Where(x =>
                        x.Name.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)
                        && x is { RowId: > 0, Name.IsEmpty: false }
                        && LazyLoot.Config.Restrictions.Duties.All(d => d.Id != x.RowId));
            },
            getItemLabel: duty => $" {duty.Name} (ID: {duty.RowId})",
            renderItem: duty =>
            {
                ImGui.Image(GetDutyIcon(duty), new Vector2(16, 16));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(duty.Name.ToString());
                ImGui.SameLine();
            },
            onSelect: duty =>
            {
                LazyLoot.Config.Restrictions.Duties.Add(new CustomRestriction
                {
                    Id = duty.RowId,
                    Enabled = true,
                    RollRule = RollResult.UnAwarded
                });
                LazyLoot.Config.Save();
            }
        );

        ImGui.PopStyleVar();

        if (ImGui.BeginPopup("import_duty_confirmation", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var validation = ValidateImport(dutySheet);
            if (!validation)
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.Text("確定要取代目前的任務限制設定嗎？");
            ImGuiEx.LineCentered(() => ImGuiEx.TextUnderlined("此操作無法復原。"));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(40 / 255f, 167 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("是", new Vector2(100f, 0)))
            {
                if (_importedRestrictions != null)
                {
                    duties = _importedRestrictions;
                    LazyLoot.Config.Save();
                    Notify.Success("已成功匯入任務限制設定！");
                }

                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(220 / 255f, 53 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("否", new Vector2(-1, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }
    }

    private static void DrawUserRestriction()
    {
        if (ImGui.BeginTabBar("PerItemDutyConfigTabs"))
        {
            if (ImGui.BeginTabItem("全部套用..."))
            {
                DrawUserRestrictionEverywhere();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("...但這些道具除外"))
            {
                DrawUserRestrictionItems();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("...但這些任務除外"))
            {
                DrawUserRestrictionDuties();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawChatAndToast()
    {
        ImGuiEx.LineCentered("ChatInfoLabel", () => ImGuiEx.TextUnderlined("擲骰結果資訊"));
        ImGui.Checkbox("在聊天欄顯示擲骰資訊。", ref LazyLoot.Config.EnableChatLogMessage);
        ImGui.Spacing();
        ImGuiEx.LineCentered("ToastLabel", () => ImGuiEx.TextUnderlined("以彈出提示顯示"));
        ImGuiComponents.HelpMarker("以下方各種樣式的彈出提示顯示你的擲骰資訊。");
        ImGui.Checkbox("任務", ref LazyLoot.Config.EnableQuestToast);
        ImGui.SameLine();
        ImGui.Checkbox("一般", ref LazyLoot.Config.EnableNormalToast);
        ImGui.SameLine();
        ImGui.Checkbox("錯誤", ref LazyLoot.Config.EnableErrorToast);
    }

    private static void DrawDtrToggle()
    {
        ImGui.Spacing();
        ImGui.Text("伺服器資訊列（DTR）");
        ImGui.Checkbox("###LazyLootDtrEnabled", ref LazyLoot.Config.ShowDtrEntry);
        ImGui.SameLine();
        ImGui.TextColored(
            LazyLoot.Config.ShowDtrEntry ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
            LazyLoot.Config.ShowDtrEntry ? "DTR 已啟用" : "DTR 已停用"
        );
        ImGui.TextWrapped("在 Dalamud 伺服器資訊列（DTR）中顯示/隱藏 LazyLoot。");
    }

    private void DrawFulf()
    {
        ImGuiEx.LineCentered("FULFLabel", () => ImGuiEx.TextUnderlined("懶人終極自動擲骰功能"));

        ImGui.TextWrapped(
            "懶人終極自動擲骰功能（FULF）是一項設定後即可自動運作的功能，會自動幫你對道具擲骰，不需再手動輸入上方的指令。");
        ImGui.Separator();
        ImGui.Columns(2, null, false);
        ImGui.SetColumnWidth(0, 80);
        ImGui.Text("/fulf need");
        ImGui.NextColumn();
        ImGui.Text("將 FULF 設為需要模式，依照 /lazy need 的規則運作。");
        ImGui.NextColumn();
        ImGui.Text("/fulf greed");
        ImGui.NextColumn();
        ImGui.Text("將 FULF 設為貪要模式，依照 /lazy greed 的規則運作。");
        ImGui.NextColumn();
        ImGui.Text("/fulf pass");
        ImGui.NextColumn();
        ImGui.Text("將 FULF 設為放棄模式，依照 /lazy pass 的規則運作。");
        ImGui.NextColumn();
        ImGui.Columns(1);
        ImGui.Separator();
        ImGui.Checkbox("###FulfEnabled", ref LazyLoot.Config.FulfEnabled);
        ImGui.SameLine();
        ImGui.TextColored(LazyLoot.Config.FulfEnabled ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
            LazyLoot.Config.FulfEnabled ? "FULF 已啟用" : "FULF 已停用");
        if (LazyLoot.Config.RestrictionWeeklyLockoutItems && LazyLoot.Config.WeeklyLockoutDutyActive)
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "偵測到每週次數限制任務：FULF 與 /lazy 擲骰已暫時停用，直到你離開此任務或停用每週次數限制設定為止。");

        ImGui.SetNextItemWidth(100);

        if (ImGui.Combo("擲骰選項", ref LazyLoot.Config.FulfRoll, new[] { "需要", "貪要", "放棄" }, 3))
            LazyLoot.Config.Save();

        ImGui.Text("首次擲骰延遲範圍（秒）");
        ImGui.SetNextItemWidth(100);
        ImGui.DragFloat("最小延遲秒數。 ", ref LazyLoot.Config.FulfMinRollDelayInSeconds, 0.1F);

        if (LazyLoot.Config.FulfMinRollDelayInSeconds >= LazyLoot.Config.FulfMaxRollDelayInSeconds)
            LazyLoot.Config.FulfMinRollDelayInSeconds = LazyLoot.Config.FulfMaxRollDelayInSeconds - 0.1f;

        if (LazyLoot.Config.FulfMinRollDelayInSeconds < 1.5f) LazyLoot.Config.FulfMinRollDelayInSeconds = 1.5f;

        ImGui.SetNextItemWidth(100);
        ImGui.DragFloat("最大延遲秒數。 ", ref LazyLoot.Config.FulfMaxRollDelayInSeconds, 0.1F);

        if (LazyLoot.Config.FulfMaxRollDelayInSeconds <= LazyLoot.Config.FulfMinRollDelayInSeconds)
            LazyLoot.Config.FulfMaxRollDelayInSeconds = LazyLoot.Config.FulfMinRollDelayInSeconds + 0.1f;
    }
}