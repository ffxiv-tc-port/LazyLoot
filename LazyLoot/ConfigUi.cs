using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
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

    /// <summary>
    /// Shown when a saved restriction points at a row the current game data has no entry for.
    /// The row itself stays visible and greyed out so the entry can still be toggled or removed:
    /// hiding it would silently drop a rule the user believes is active.
    /// </summary>
    private static string UnknownIdTooltip =>
        "This ID does not exist in the current game data, so this rule never matches anything. It usually comes from a list exported on a different game version. Use Remove to delete it.".Loc();

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

    public ConfigUi() : base("Lazy Loot Config".Loc())
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
            if (ImGui.BeginTabItem("Features".Loc()))
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

            if (ImGui.BeginTabItem("User Restriction".Loc()))
            {
                DrawUserRestriction();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("About".Loc()))
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
        ImGuiEx.LineCentered("DiagnosticsLabel", () => ImGuiEx.TextUnderlined("Diagnostics & Troubleshooting".Loc()));

        if (ImGui.Checkbox("Diagnostics Mode".Loc(), ref LazyLoot.Config.DiagnosticsMode))
            LazyLoot.Config.Save();

        ImGuiComponents.HelpMarker(
            "Outputs additional messages to chat whenever an item is passed, with reasons. This is useful for helping to diagnose issues with the developers or for understanding why LazyLoot makes decisions to pass on items.\n\nThese messages will only be displayed to you, nobody else in-game can see them.".Loc());

        if (ImGui.Checkbox("Don't pass on items that fail to roll.".Loc(), ref LazyLoot.Config.NoPassEmergency))
            LazyLoot.Config.Save();

        ImGuiComponents.HelpMarker(
            "Normally LazyLoot will pass on items that fail to roll. Enabling this option will prevent it from passing in those situations. Be warned there could be weird side effects doing this and should only be used if you're running into issues with emergency passing appearing.".Loc());
    }

    public override void OnClose()
    {
        LazyLoot.Config.Save();
        base.OnClose();
    }

    private static void DrawFeatures()
    {
        ImGuiEx.LineCentered("FeaturesLabel", () => ImGuiEx.TextUnderlined("LazyLoot Rolling Commands".Loc()));
        ImGui.Columns(2, ImU8String.Empty, false);
        ImGui.SetColumnWidth(0, 80);
        ImGui.Text("/lazy need");
        ImGui.NextColumn();
        ImGui.Text("Roll need for everything. If impossible, roll greed (or pass if greed is impossible).".Loc());
        ImGui.NextColumn();
        ImGui.Text("/lazy greed");
        ImGui.NextColumn();
        ImGui.Text("Roll greed for everything. If impossible, roll pass.".Loc());
        ImGui.NextColumn();
        ImGui.Text("/lazy pass");
        ImGui.NextColumn();
        ImGui.Text("Pass on things you haven't rolled for yet.".Loc());
        ImGui.NextColumn();
        ImGui.Columns(1);
    }

    private static void DrawRollingDelay()
    {
        ImGuiEx.LineCentered("RollingDelayLabel", () => ImGuiEx.TextUnderlined("Rolling Command Delay".Loc()));
        ImGui.SetNextItemWidth(100);

        if (ImGui.DragFloatRange2("Rolling delay between items".Loc(), ref LazyLoot.Config.MinRollDelayInSeconds,
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
            "Only Untradeables".Loc(),
            ref thisRestriction);
        ImGui.Unindent(20f);
        ImGui.PopID();
    }

    private static void DrawUserRestrictionEverywhere()
    {
        ImGui.TextWrapped("Settings in this page will apply to every single item, even if they are tradeable or not.".Loc());
        ImGui.Separator();
        ImGui.Checkbox("Pass on items with an item level below".Loc(),
            ref LazyLoot.Config.RestrictionIgnoreItemLevelBelow);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(50);
        ImGui.DragInt("###RestrictionIgnoreItemLevelBelowValue",
            ref LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue);
        if (LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue < 0)
            LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue = 0;

        Utils.CheckboxTextWrapped(
            "Pass on all items already unlocked. (Triple Triad Cards, Orchestrions, Faded Copies, Minions, Mounts, Emotes, Hairstyles)".Loc(),
            ref LazyLoot.Config.RestrictionIgnoreItemUnlocked);

        if (!LazyLoot.Config.RestrictionIgnoreItemUnlocked)
        {
            ImGui.Checkbox("Pass on unlocked Mounts.".Loc(), ref LazyLoot.Config.RestrictionIgnoreMounts);
            DrawOnlyUntradeableCheckbox(
                "RestrictionMountsOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreMounts,
                ref LazyLoot.Config.RestrictionMountsOnlyUntradeables
            );

            ImGui.Checkbox("Pass on unlocked Minions.".Loc(), ref LazyLoot.Config.RestrictionIgnoreMinions);
            DrawOnlyUntradeableCheckbox(
                "RestrictionMinionsOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreMinions,
                ref LazyLoot.Config.RestrictionMinionsOnlyUntradeables
            );

            ImGui.Checkbox("Pass on unlocked Bardings.".Loc(), ref LazyLoot.Config.RestrictionIgnoreBardings);
            DrawOnlyUntradeableCheckbox(
                "RestrictionBardingsOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreBardings,
                ref LazyLoot.Config.RestrictionBardingsOnlyUntradeables
            );

            ImGui.Checkbox("Pass on unlocked Triple Triad cards.".Loc(),
                ref LazyLoot.Config.RestrictionIgnoreTripleTriadCards);
            DrawOnlyUntradeableCheckbox(
                "RestrictionTripleTriadCardsOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreTripleTriadCards,
                ref LazyLoot.Config.RestrictionTripleTriadCardsOnlyUntradeables
            );

            ImGui.Checkbox("Pass on unlocked Emotes and Hairstyle.".Loc(),
                ref LazyLoot.Config.RestrictionIgnoreEmoteHairstyle);
            DrawOnlyUntradeableCheckbox(
                "RestrictionEmoteHairstyleOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreEmoteHairstyle,
                ref LazyLoot.Config.RestrictionEmoteHairstyleOnlyUntradeables
            );

            ImGui.Checkbox("Pass on unlocked Orchestrion Rolls.".Loc(),
                ref LazyLoot.Config.RestrictionIgnoreOrchestrionRolls);
            DrawOnlyUntradeableCheckbox(
                "RestrictionOrchestrionRollsOnlyUntradeables",
                ref LazyLoot.Config.RestrictionIgnoreOrchestrionRolls,
                ref LazyLoot.Config.RestrictionOrchestrionRollsOnlyUntradeables
            );

            ImGui.Checkbox("Pass on unlocked Faded Copies.".Loc(), ref LazyLoot.Config.RestrictionIgnoreFadedCopy);
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

        ImGui.Checkbox("Pass on items I can't use with current job.".Loc(),
            ref LazyLoot.Config.RestrictionOtherJobItems);

        ImGui.Checkbox("Don't roll on items or duties with a weekly lockout.".Loc(),
            ref LazyLoot.Config.RestrictionWeeklyLockoutItems);

        ImGui.Checkbox("###RestrictionWeeklyLockoutItems", ref LazyLoot.Config.RestrictionLootLowerThanJobIlvl);
        ImGui.SameLine();
        ImGui.Text("Roll".Loc());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("###RestrictionLootLowerThanJobIlvlRollState",
            ref LazyLoot.Config.RestrictionLootLowerThanJobIlvlRollState, new[] { "Greed".Loc(), "Pass".Loc() }, 2);
        ImGui.SameLine();
        ImGui.Text("on items that are".Loc());
        ImGui.SetNextItemWidth(50);
        ImGui.SameLine();
        ImGui.DragInt("###RestrictionLootLowerThanJobIlvlTreshold",
            ref LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold);
        if (LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold < 0)
            LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold = 0;
        ImGui.SameLine();
        ImGui.Text("item levels lower than your current job item level (\u2605 ??).".Loc(Utils.GetPlayerIlevel()));
        ImGuiComponents.HelpMarker("This setting will only apply to gear you can need on.".Loc());

        ImGui.Checkbox("###RestrictionLootIsJobUpgrade", ref LazyLoot.Config.RestrictionLootIsJobUpgrade);
        ImGui.SameLine();
        ImGui.Text("Roll".Loc());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("###RestrictionLootIsJobUpgradeRollState",
            ref LazyLoot.Config.RestrictionLootIsJobUpgradeRollState,
            new[] { "Greed".Loc(), "Pass".Loc() }, 2);
        ImGui.SameLine();
        ImGui.Text("on items if the current equipped item of the same type has a higher item level.".Loc());
        ImGuiComponents.HelpMarker("This setting will only apply to gear you can need on.".Loc());

        ImGui.Checkbox("###RestrictionSeals", ref LazyLoot.Config.RestrictionSeals);
        ImGui.SameLine();
        ImGui.Text("Pass on items with an expert delivery seal value of less than".Loc());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.DragInt("###RestrictionSealsAmnt", ref LazyLoot.Config.RestrictionSealsAmnt);
        ImGui.SameLine();
        ImGui.Text("(item level ?? and below)".Loc(Roller.ConvertSealsToIlvl(LazyLoot.Config.RestrictionSealsAmnt)));
        ImGuiComponents.HelpMarker(
            "This setting will only apply to gear able to be turned in for expert delivery.".Loc());

        ImGui.Checkbox("###NeverPassGlam", ref LazyLoot.Config.NeverPassGlam);
        ImGui.SameLine();
        ImGui.TextWrapped("Never pass on glamour items (Items that have an item and iLvl of 1)".Loc());
    }

    private static void CenterText()
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetColumnWidth() - ImGui.GetFrameHeight()) * 0.5f);
    }

    /// <summary>
    /// Ultimate duties are stored as ContentType 5 (raids) plus the HighEndDuty flag, so they are
    /// remapped onto ContentType 28 ("Ultimate") purely for display.
    /// <para>
    /// RowRef.Value throws InvalidOperationException when the reference does not resolve, and this
    /// runs on the ImGui Draw path where a single throw makes UiBuilder null the whole Draw
    /// delegate (the plugin UI never comes back until the game restarts), so every lookup here
    /// goes through ValueNullable/TryGetRow.
    /// </para>
    /// </summary>
    private static bool TryGetDisplayContentType(ContentFinderCondition duty, out ContentType contentType)
    {
        var direct = duty.ContentType.ValueNullable;
        if (duty.HighEndDuty && direct?.RowId == 5
            && Svc.Data.GetExcelSheet<ContentType>().TryGetRow(28, out var ultimate))
        {
            contentType = ultimate;
            return true;
        }

        contentType = direct ?? default;
        return direct != null;
    }

    private static ImTextureID GetDutyIcon(ContentFinderCondition duty)
    {
        if (!TryGetDisplayContentType(duty, out var contentType))
            return 0;

        var icon = contentType.Icon;
        if (icon == 0)
        {
            return 0;
        }

        var itemIcon = GetItemIcon(icon);
        return itemIcon?.Handle ?? default;
    }

    private static string GetDutyTypeName(ContentFinderCondition duty)
    {
        return TryGetDisplayContentType(duty, out var contentType)
            ? contentType.Name.ToString()
            : string.Empty;
    }

    private static void DrawUserRestrictionItems()
    {
        ImGui.Dummy(new Vector2(0, 6));
        ImGuiEx.LineCentered("ItemRestrictionWarning",
            () =>
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                ImGui.TextWrapped("These rules override any other restriction settings, but the weekly lockout.".Loc());
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
                ImGuiEx.TextCentered("No items added.".Loc());
                ImGuiEx.TextCentered("Click the Add item below to start adding items.".Loc());
                ImGui.EndChild();
            }

            ImGui.PopStyleVar();
            ImGui.Dummy(new Vector2(0, 6));
        }
        else
        {
            if (ImGui.BeginTable("UserRestrictionItemsTable", 8, ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("Enabled".Loc(), ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Icon".Loc(), ImGuiTableColumnFlags.WidthFixed, 32f);
                ImGui.TableSetupColumn("Name".Loc(), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Need".Loc(), ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Greed".Loc(), ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Pass".Loc(), ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Nothing".Loc(), ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableHeadersRow();

                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    // TryGetRow, never GetRow: ids can arrive from an import made on another
                    // game version, and a throw on the Draw path kills the whole plugin UI.
                    var itemKnown = Svc.Data.GetExcelSheet<Item>().TryGetRow(item.Id, out var restrictedItem);
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

                    var icon = itemKnown ? GetItemIcon(restrictedItem.Icon) : null;
                    if (icon != null)
                        ImGui.Image(icon.Handle, new Vector2(24, 24));
                    else
                        ImGui.Text(itemKnown ? "-" : "?");

                    ImGui.TableNextColumn();
                    if (itemKnown)
                    {
                        ImGui.Text(restrictedItem.Name.ToString());
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(restrictedItem.Name.ToString());
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.DalamudGrey, "Unknown item (ID: ??)".Loc(item.Id));
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(UnknownIdTooltip);
                    }

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
                    if (ImGui.Button("Remove".Loc() + $"##{item.Id}"))
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
        if (ImGui.Button("Export".Loc(), new Vector2(60, 0)))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(LazyLoot.Config.Restrictions.Items);
            ImGui.SetClipboardText(json);
            Notify.Success("Item Restrictions settings copied to clipboard!".Loc());
        }

        ImGui.SameLine();
        if (ImGui.Button("Import".Loc(), new Vector2(60, 0)))
        {
            try
            {
                bool userImported = ImportFromClipboard("import_item_confirmation");
                if (!userImported)
                {
                    Notify.Error("Failed to import item restriction settings - invalid format".Loc());
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
            buttonLabel: "Add item...".Loc(),
            popupId: "item_search_add",
            popupTitle: "Search for item:".Loc(),
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
                    ImGui.Image(icon.Handle, new Vector2(16, 16));
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

            ImGui.Text("Are you sure you want to replace your current item restrictions configuration?".Loc());
            ImGuiEx.LineCentered(() => ImGuiEx.TextUnderlined("This action cannot be undone.".Loc()));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(40 / 255f, 167 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("YES".Loc(), new Vector2(100f, 0)))
            {
                if (_importedRestrictions != null)
                {
                    LazyLoot.Config.Restrictions.Items = _importedRestrictions;
                    LazyLoot.Config.Save();
                    Notify.Success("Imported Item Restrictions successfully!".Loc());
                }

                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(220 / 255f, 53 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("NO".Loc(), new Vector2(-1, 0)))
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
            // HasRow is an O(1) lookup. This runs every frame the confirmation popup is open,
            // and the old `sheet.Any(x => x.RowId == item.Id)` was a linear scan of the whole
            // sheet (~49k rows for Item) per imported entry per frame.
            if (sheet.HasRow(item.Id)) continue;
            bail = true;
            Notify.Error("Imported restriction contains invalid item ID: ??. Import cancelled.".Loc(item.Id));
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
            Notify.Error("Nothing to import on your clipboard".Loc());
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
                ImGui.TextWrapped("These rules override the main restriction settings, but is overriden by the item restriction settings if they happen to collide.".Loc());
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
                ImGuiEx.TextCentered("No duties added.".Loc());
                ImGuiEx.TextCentered("Click the Add duty below to start adding duties.".Loc());
                ImGui.EndChild();
            }

            ImGui.PopStyleVar();
            ImGui.Dummy(new Vector2(0, 6));
        }
        else
        {
            if (ImGui.BeginTable("UserRestrictionDutiesTable", 8, ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("Enabled".Loc(), ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Type".Loc(), ImGuiTableColumnFlags.WidthFixed, 32f);
                ImGui.TableSetupColumn("Name".Loc(), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Need".Loc(), ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Greed".Loc(), ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Pass".Loc(), ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Nothing".Loc(), ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableHeadersRow();

                for (var i = 0; i < duties.Count; i++)
                {
                    var duty = duties[i];
                    // TryGetRow, never GetRow: see the item table above.
                    var dutyKnown = Svc.Data.GetExcelSheet<ContentFinderCondition>()
                        .TryGetRow(duty.Id, out var restrictedDuty);
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

                    if (dutyKnown)
                    {
                        ImGui.Image(GetDutyIcon(restrictedDuty), new Vector2(24, 24));
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(GetDutyTypeName(restrictedDuty));
                    }
                    else
                    {
                        ImGui.Text("?");
                    }

                    ImGui.TableNextColumn();
                    if (dutyKnown)
                    {
                        ImGui.Text(restrictedDuty.Name.ToString());
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(restrictedDuty.Name.ToString());
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.DalamudGrey, "Unknown duty (ID: ??)".Loc(duty.Id));
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(UnknownIdTooltip);
                    }

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
                    if (ImGui.Button("Remove".Loc() + $"##{duty.Id}"))
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
        if (ImGui.Button("Export".Loc(), new Vector2(60, 0)))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(LazyLoot.Config.Restrictions.Duties);
            ImGui.SetClipboardText(json);
            Notify.Success("Duty Restrictions copied to clipboard!".Loc());
        }

        ImGui.SameLine();
        if (ImGui.Button("Import".Loc(), new Vector2(60, 0)))
        {
            try
            {
                bool userImported = ImportFromClipboard("import_duty_confirmation");
                if (!userImported)
                {
                    Notify.Error("Failed to import duty restriction settings - invalid format".Loc());
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
            buttonLabel: "Add duty...".Loc(),
            popupId: "duty_search_add",
            popupTitle: "Search for duty:".Loc(),
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

            ImGui.Text("Are you sure you want to replace your current duty restrictions configuration?".Loc());
            ImGuiEx.LineCentered(() => ImGuiEx.TextUnderlined("This action cannot be undone.".Loc()));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(40 / 255f, 167 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("YES".Loc(), new Vector2(100f, 0)))
            {
                if (_importedRestrictions != null)
                {
                    // Was `duties = _importedRestrictions;`, which only reassigned the local
                    // captured at the top of this method: the import reported success, saved the
                    // config and changed nothing. The item path below/above always did this right.
                    LazyLoot.Config.Restrictions.Duties = _importedRestrictions;
                    LazyLoot.Config.Save();
                    Notify.Success("Imported Duty Restrictions successfully!".Loc());
                }

                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(220 / 255f, 53 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("NO".Loc(), new Vector2(-1, 0)))
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
            if (ImGui.BeginTabItem("Everywhere...".Loc()))
            {
                DrawUserRestrictionEverywhere();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("... but for these Items".Loc()))
            {
                DrawUserRestrictionItems();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("... but for these Duties".Loc()))
            {
                DrawUserRestrictionDuties();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawChatAndToast()
    {
        ImGuiEx.LineCentered("ChatInfoLabel", () => ImGuiEx.TextUnderlined("Roll Result Information".Loc()));
        ImGui.Checkbox("Display roll information in chat.".Loc(), ref LazyLoot.Config.EnableChatLogMessage);
        ImGui.Spacing();
        ImGuiEx.LineCentered("ToastLabel", () => ImGuiEx.TextUnderlined("Display as Toasts".Loc()));
        ImGuiComponents.HelpMarker("Show your roll information as a pop-up toast, using the various styles below.".Loc());
        ImGui.Checkbox("Quest".Loc(), ref LazyLoot.Config.EnableQuestToast);
        ImGui.SameLine();
        ImGui.Checkbox("Normal".Loc(), ref LazyLoot.Config.EnableNormalToast);
        ImGui.SameLine();
        ImGui.Checkbox("Error".Loc(), ref LazyLoot.Config.EnableErrorToast);
    }

    private static void DrawDtrToggle()
    {
        ImGui.Spacing();
        ImGui.Text("Server Info Bar (DTR)".Loc());
        ImGui.Checkbox("###LazyLootDtrEnabled", ref LazyLoot.Config.ShowDtrEntry);
        ImGui.SameLine();
        ImGui.TextColored(
            LazyLoot.Config.ShowDtrEntry ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
            LazyLoot.Config.ShowDtrEntry ? "DTR Enabled".Loc() : "DTR Disabled".Loc()
        );
        ImGui.TextWrapped("Show/hide LazyLoot in the Dalamud Server Info Bar (DTR).".Loc());
    }

    private void DrawFulf()
    {
        ImGuiEx.LineCentered("FULFLabel", () => ImGuiEx.TextUnderlined("Fancy Ultimate Lazy Feature".Loc()));

        ImGui.TextWrapped(
            "Fancy Ultimate Lazy Feature (FULF) is a set and forget feature that will automatically roll on items for you instead of having to use the commands above.".Loc());
        ImGui.Separator();
        ImGui.Columns(2, ImU8String.Empty, false);
        ImGui.SetColumnWidth(0, 80);
        ImGui.Text("/fulf need");
        ImGui.NextColumn();
        ImGui.Text("Set FULF to Needing mode, where it will follow the /lazy need rules.".Loc());
        ImGui.NextColumn();
        ImGui.Text("/fulf greed");
        ImGui.NextColumn();
        ImGui.Text("Set FULF to Greeding mode, where it will follow the /lazy greed rules.".Loc());
        ImGui.NextColumn();
        ImGui.Text("/fulf pass");
        ImGui.NextColumn();
        ImGui.Text("Set FULF to Passing mode, where it will follow the /lazy pass rules.".Loc());
        ImGui.NextColumn();
        ImGui.Columns(1);
        ImGui.Separator();
        ImGui.Checkbox("###FulfEnabled", ref LazyLoot.Config.FulfEnabled);
        ImGui.SameLine();
        ImGui.TextColored(LazyLoot.Config.FulfEnabled ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
            LazyLoot.Config.FulfEnabled ? "FULF Enabled".Loc() : "FULF Disabled".Loc());
        if (LazyLoot.Config.RestrictionWeeklyLockoutItems && LazyLoot.Config.WeeklyLockoutDutyActive)
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "Weekly Lockout Duty detected: FULF and /lazy rolls are temporarily disabled until you leave this duty or disable the weekly lockout setting.".Loc());

        ImGui.SetNextItemWidth(100);

        if (ImGui.Combo("Roll options".Loc(), ref LazyLoot.Config.FulfRoll, new[] { "Need".Loc(), "Greed".Loc(), "Pass".Loc() }, 3))
            LazyLoot.Config.Save();

        ImGui.Text("First Roll Delay Range (In seconds)".Loc());
        ImGui.SetNextItemWidth(100);
        ImGui.DragFloat("Minimum Delay in seconds. ".Loc(), ref LazyLoot.Config.FulfMinRollDelayInSeconds, 0.1F);

        if (LazyLoot.Config.FulfMinRollDelayInSeconds >= LazyLoot.Config.FulfMaxRollDelayInSeconds)
            LazyLoot.Config.FulfMinRollDelayInSeconds = LazyLoot.Config.FulfMaxRollDelayInSeconds - 0.1f;

        if (LazyLoot.Config.FulfMinRollDelayInSeconds < 1.5f) LazyLoot.Config.FulfMinRollDelayInSeconds = 1.5f;

        ImGui.SetNextItemWidth(100);
        ImGui.DragFloat("Maximum Delay in seconds. ".Loc(), ref LazyLoot.Config.FulfMaxRollDelayInSeconds, 0.1F);

        if (LazyLoot.Config.FulfMaxRollDelayInSeconds <= LazyLoot.Config.FulfMinRollDelayInSeconds)
            LazyLoot.Config.FulfMaxRollDelayInSeconds = LazyLoot.Config.FulfMinRollDelayInSeconds + 0.1f;
    }
}