using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using ECommons.Logging;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using PunishLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LazyLoot;

public class LazyLoot : IDalamudPlugin, IDisposable
{
    private static readonly RollResult[] RollArray =
    [
        RollResult.Needed,
        RollResult.Greeded,
        RollResult.Passed
    ];

    public string Name => "LazyLoot";

    internal static Configuration Config;
    private static ConfigUi _configUi;
    private static IDtrBarEntry _dtrEntry;

    static DateTime _nextRollTime = DateTime.Now;
    static RollResult _rollOption = RollResult.UnAwarded;
    private static int _need, _greed, _pass;

    private const uint CastYourLotMessage = 5194;
    private const uint WeeklyLockoutMessage = 4234;

    public LazyLoot(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this);
        ECommons.LanguageHelpers.Localization.Init("ChineseTraditional");
        PunishLibMain.Init(pluginInterface, "LazyLoot", new AboutPlugin() { Developer = "53m1k0l0n/Gidedin" });

        Config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _configUi = new ConfigUi();
        _dtrEntry = Svc.DtrBar.Get("LazyLoot");
        _dtrEntry.OnClick = OnDtrClick;


        Svc.PluginInterface.UiBuilder.OpenMainUi += OnOpenConfigUi;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
        Svc.Chat.ChatMessage += NoticeLoot;
        // ⚠️ 診斷刻意獨立訂閱，不掛在 NoticeLoot 裡面 ——
        //    NoticeLoot 第一件事就是 `if (!Config.FulfEnabled) return;`，
        //    掛進去等於讓診斷被 FULF 開關靜默吃掉。
        // 🔴 預設關閉，只有使用者自己打開（設定頁或 /lazy diag on）才會訂閱。
        RollDiagnostics.Refresh();
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        SyncWeeklyLockoutDutyState(Svc.ClientState.TerritoryType);

        Svc.Commands.AddHandler("/lazyloot", new CommandInfo(LazyCommand)
        {
            HelpMessage = "Open Lazy Loot config.".Loc(),
            ShowInHelp = true,
        });

        Svc.Commands.AddHandler("/lazy", new CommandInfo(LazyCommand)
        {
            HelpMessage = "Open Lazy Loot config by default. Add need | greed | pass to roll on current items.".Loc(),
            ShowInHelp = true,
        });

        Svc.Commands.AddHandler("/fulf", new CommandInfo(FulfCommand)
        {
            HelpMessage =
                "Enable/Disable FULF with /fulf [on|off] or change the loot rule with /fulf need | greed | pass.".Loc(),
            ShowInHelp = true,
        });

        Svc.Framework.Update += OnFrameworkUpdate;
    }

    private static void OnDtrClick(DtrInteractionEvent ev)
    {
        // ⚠️ 這個外掛的右鍵已經有用途（反向切換拾取規則），所以「開關視窗」維持在 Ctrl+點擊，
        // 不像其他外掛那樣綁右鍵 —— 把一個能用的功能換掉，比少一個捷徑糟。
        // 由「只開啟」改成「開關」：再按一次會關掉。
        if (ev.ModifierKeys.HasFlag(ClickModifierKeys.Ctrl))
        {
            _configUi.IsOpen ^= true;
            return;
        }

        switch (ev.ClickType)
        {
            case MouseClickType.Left:
                CycleFulf(true);
                break;

            case MouseClickType.Right:
                CycleFulf(false);
                break;
        }
    }

    private void LazyCommand(string command, string arguments)
    {
        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0)
        {
            OnOpenConfigUi();
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "test" when args.Length >= 2:
                {
                    switch (args[1].ToLowerInvariant())
                    {
                        case "item":
                            TestWhatWouldLlDo(string.Join(" ", args.Skip(2)));
                            return;
                    }

                    break;
                }
            case "diag":
                ToggleRollDiagnostics(args.Length >= 2 ? args[1] : null);
                return;
            default:
                RollingCommand(null!, arguments);
                return;
        }
    }

    /// <summary>
    /// <c>/lazy diag [on|off]</c>：切換擲骰診斷記錄。沒帶參數就是反向切換。
    /// ⚠️ 開著時每一句系統訊息都會寫一行 Information，只在要調查時才開。
    /// </summary>
    private static void ToggleRollDiagnostics(string? argument)
    {
        var wanted = argument?.ToLowerInvariant() switch
        {
            "on" or "true" or "1" => true,
            "off" or "false" or "0" => false,
            _ => !Config.RollDiagnosticsLogging,
        };

        Config.RollDiagnosticsLogging = wanted;
        Config.Save();
        RollDiagnostics.Refresh();

        Svc.Chat.Print(new SeString(new List<Payload>
        {
            new TextPayload("[LazyLoot] "),
            new TextPayload(wanted
                ? "Roll diagnostics logging is now ON. This writes a log line for every system message - turn it off when you are done.".Loc()
                : "Roll diagnostics logging is now OFF.".Loc()),
        }));
    }

    private static void CycleFulf(bool forward)
    {
        if (!Config.FulfEnabled)
        {
            Config.FulfEnabled = true;
            Config.FulfRoll = forward ? 2 : 0;
            Config.Save();
            return;
        }

        if (forward)
        {
            switch (Config.FulfRoll)
            {
                case 2:
                    Config.FulfRoll = 1;
                    break;
                case 1:
                    Config.FulfRoll = 0;
                    break;
                default:
                    Config.FulfEnabled = false;
                    break;
            }
        }
        else
        {
            switch (Config.FulfRoll)
            {
                case 0:
                    Config.FulfRoll = 1;
                    break;
                case 1:
                    Config.FulfRoll = 2;
                    break;
                default:
                    Config.FulfEnabled = false;
                    break;
            }
        }

        Config.Save();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        Svc.PluginInterface.UiBuilder.OpenMainUi -= OnOpenConfigUi;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        Svc.Chat.ChatMessage -= NoticeLoot;
        RollDiagnostics.Disable();
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;

        Svc.Commands.RemoveHandler("/lazyloot");
        Svc.Commands.RemoveHandler("/lazy");
        Svc.Commands.RemoveHandler("/fulf");

        ECommonsMain.Dispose();
        PunishLibMain.Dispose();
        Svc.Log.Information(">>Stop LazyLoot<<");
        _dtrEntry.Remove();

        Svc.Framework.Update -= OnFrameworkUpdate;
        Config.Save();
    }

    private static void FulfCommand(string command, string arguments)
    {
        var res = GetResult(arguments);
        if (res.HasValue)
            Config.FulfRoll = res.Value;
        else if (arguments.Contains("off", StringComparison.CurrentCultureIgnoreCase))
            Config.FulfEnabled = false;
        else if (arguments.Contains("on", StringComparison.CurrentCultureIgnoreCase))
            Config.FulfEnabled = true;
        else
            Config.FulfEnabled = !Config.FulfEnabled;
    }

    private void RollingCommand(string command, string arguments)
    {
        var res = GetResult(arguments);
        if (res.HasValue)
        {
            _rollOption = RollArray[res.Value % 3];
        }
    }

    private static int? GetResult(string str)
    {
        if (str.Contains("need", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        else if (str.Contains("greed", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        else if (str.Contains("pass", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return null;
    }

    private static void OnOpenConfigUi()
    {
        _configUi.Toggle();
    }

    private static void OnFrameworkUpdate(IFramework framework)
    {
        // DTR 這一格的版面預算只有一個字：列上只放「現在會怎麼骰」，
        // 完整模式名稱與點擊操作一律進 tooltip。
        // ⚠️ 這幾個字刻意寫死中文而不走 .Loc()：這個 fork 在建構式裡固定
        //    Localization.Init("ChineseTraditional") 且沒有語言選單，
        //    寫死才能在同一行看出「哪個字對哪個狀態」——對錯會害使用者骰錯東西。
        var isWeeklyLockedDutyActive = Config is { RestrictionWeeklyLockoutItems: true, WeeklyLockoutDutyActive: true };

        string modeShort, modeFull;
        if (Config.FulfEnabled)
        {
            (modeShort, modeFull) = Config.FulfRoll switch
            {
                0 => ("需", "需求（Need）"),
                1 => ("貪", "貪婪（Greed）"),
                2 => ("跳", "放棄（Pass）"),
                _ => throw new ArgumentOutOfRangeException(nameof(Config.FulfRoll)),
            };
        }
        else
        {
            (modeShort, modeFull) = ("停", "已停用（不會自動擲骰）");
        }

        // 週限任務暫停時原本是在模式後面接一長串「（已停用 | WLD）」。
        // ⚠️ 這個狀態不能只藏進 tooltip —— 列上還顯示「需」但其實一顆都不會骰，
        //    比顯示錯的資訊更糟。改成整格換成「鎖」，維持單字寬度又能一眼看出被暫停。
        var dtrText = isWeeklyLockedDutyActive ? "鎖" : modeShort;

        _dtrEntry.Text = new SeString(
            new IconPayload(BitmapFontIcon.Dice),
            new TextPayload(dtrText));

        // 這一格原本完全沒有提示，滑鼠移上去什麼都不會出現。
        // ⚠️ 右鍵是「反向切換規則」而不是開視窗（其他外掛的右鍵才是開關視窗），
        // 所以提示必須把這個差異講清楚，否則使用者會以為是壞的。
        _dtrEntry.Tooltip = new SeString(new TextPayload(
            $"LazyLoot 自動擲骰\n目前：{modeFull}\n"
            + (isWeeklyLockedDutyActive ? "鎖：本週次數上限任務中，擲骰暫停\n" : "")
            + "\n需：需求／貪：貪婪／跳：放棄／停：停用\n\n"
            + "左鍵：切換到下一個規則\n"
            + "右鍵：切換到上一個規則\n"
            + "Ctrl+點擊：開啟／關閉設定視窗"));

        _dtrEntry.Shown = Config.ShowDtrEntry;

        if (isWeeklyLockedDutyActive) return;

        RollLoot();
    }

    private static void RollLoot()
    {
        if (_rollOption == RollResult.UnAwarded) return;
        if (DateTime.Now < _nextRollTime) return;

        //No rolling in cutscene.
        if (Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return;

        _nextRollTime = DateTime.Now.AddMilliseconds(Math.Max(1500, new Random()
            .Next((int)(Config.MinRollDelayInSeconds * 1000),
                (int)(Config.MaxRollDelayInSeconds * 1000))));

        try
        {
            if (Roller.RollOneItem(_rollOption, ref _need, ref _greed, ref _pass)) return; //Finish the loot
            ShowResult(_need, _greed, _pass);
            _need = _greed = _pass = 0;
            _rollOption = RollResult.UnAwarded;
            Roller.Clear();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Something Wrong with rolling!");
        }
    }

    private static void ShowResult(int need, int greed, int pass)
    {
        SeString seString = new(new List<Payload>()
        {
            new TextPayload("Need ".Loc()),
            new UIForegroundPayload(575),
            new TextPayload(need.ToString()),
            new UIForegroundPayload(0),
            new TextPayload((" item" + (need == 1 ? "" : "s") + ", greed ").Loc()),
            new UIForegroundPayload(575),
            new TextPayload(greed.ToString()),
            new UIForegroundPayload(0),
            new TextPayload((" item" + (greed == 1 ? "" : "s") + ", pass ").Loc()),
            new UIForegroundPayload(575),
            new TextPayload(pass.ToString()),
            new UIForegroundPayload(0),
            new TextPayload((" item" + (pass == 1 ? "" : "s") + ".").Loc())
        });

        if (Config.EnableChatLogMessage)
        {
            Svc.Chat.Print(seString);
        }

        if (Config.EnableErrorToast)
        {
            Svc.Toasts.ShowError(seString);
        }

        if (Config.EnableNormalToast)
        {
            Svc.Toasts.ShowNormal(seString);
        }

        if (Config.EnableQuestToast)
        {
            Svc.Toasts.ShowQuest(seString);
        }
    }

    private void NoticeLoot(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        // 🔴 這一行原本無條件執行：每一句聊天都做一次字串內插並寫入 log，
        //    是 LazyLoot 在實機 log 裡的最大宗來源。內插是先算再交給 log，
        //    所以「等級關著就免費」不成立 —— 必須在外面用旗標擋掉。
        //    共用「擲骰診斷記錄」這個開關，要調查時一起開。
        if (Config.RollDiagnosticsLogging)
            Svc.Log.Debug($"{type} {message}");
        // TC note: the "cast your lot" roll prompt arrives as a different, undocumented
        // chat type on TC (observed as raw type 2105 in logs, not XivChatType.SystemMessage
        // like on global) - matching purely on chat type silently dropped every roll prompt
        // and LazyLoot never auto-rolled. The message-text comparison below is unique enough
        // on its own, so the chat-type gate is dropped rather than hardcoding TC's type value.
        if (!Config.FulfEnabled) return;

        // 🔴 兩端剝 SeString 的實作必須是同一套。右邊是 Lumina 的 ReadOnlySeString,
        // 直接拿 string 去比會走 ReadOnlySeString 的 implicit operator(string) 把左邊包回
        // ReadOnlySeString,Equals 是 Data.SequenceEqual ⇒ 那是**原始位元組序列比對**,
        // 句子裡只要有一個 payload(連字符、換行、自動翻譯)就恆為 false。
        // 而 message.TextValue 本身又是第三套剝法(Dalamud 的 SeHyphenPayload 把 0x1F 吐成
        // U+2013 的 en dash,Lumina 吐的是 U+002D 的 '-')。
        // ⇒ 兩端一律先回到 Lumina 的 ExtractText() 再比字串。
        var messageText = new ReadOnlySeStringSpan(message.Encode()).ExtractText();

        // do a few checks to see if the message is the weekly lockout message the game sends
        if (CheckAndUpdateWeeklyLockoutDutyFlag(messageText)) return;
        // if not Cast your lot, then just ignore
        // 原為 First(x => x.RowId == CastYourLotMessage):那是對整張 LogMessage 表做線性
        // 掃描找主鍵,而這條路徑是**每一則聊天訊息**都會走到。GetRowOrDefault 是索引查詢,
        // 而且列不存在時回 null 而不是擲 InvalidOperationException。
        var castYourLot = Svc.Data.GetExcelSheet<LogMessage>().GetRowOrDefault(CastYourLotMessage);
        if (castYourLot == null || messageText != castYourLot.Value.Text.ExtractText()) return;
        _nextRollTime = DateTime.Now.AddMilliseconds(new Random()
            .Next((int)(Config.FulfMinRollDelayInSeconds * 1000),
                (int)(Config.FulfMaxRollDelayInSeconds * 1000)));
        _rollOption = RollArray[Config.FulfRoll];
    }

    /// <summary>
    /// <paramref name="messageText" /> 必須是呼叫端用 Lumina 的 ExtractText() 抽出來的文字
    /// —— 下面要拿它跟 Lumina 的資料表欄位比,兩端的剝法不一致就會恆不相等。
    /// </summary>
    private static bool CheckAndUpdateWeeklyLockoutDutyFlag(string messageText)
    {
        if (!Config.RestrictionWeeklyLockoutItems || Config.WeeklyLockoutDutyActive)
            return false;

        if (!IsHighEndDutyTerritory(Svc.ClientState.TerritoryType))
        {
            ClearWeeklyLockoutDutyState();
            return false;
        }

        var weeklyLockoutMessage = Svc.Data.GetExcelSheet<LogMessage>().GetRowOrDefault(WeeklyLockoutMessage);
        if (weeklyLockoutMessage == null)
            return false;

        if (messageText != weeklyLockoutMessage.Value.Text.ExtractText()) return false;

        Config.WeeklyLockoutDutyActive = true;
        Config.WeeklyLockoutDutyTerritoryId = (ushort)Svc.ClientState.TerritoryType; //Casting this as to not fuck with configs
        Config.Save();
        DuoLog.Debug("Weekly lockout duty detected! Rolling is temporarily suspended.");

        return true;
    }

    private static void OnTerritoryChanged(ushort territoryId)
    {
        if (!IsHighEndDutyTerritory(territoryId))
        {
            ClearWeeklyLockoutDutyState();
            return;
        }

        if (!Config.WeeklyLockoutDutyActive)
            return;

        if (Config.WeeklyLockoutDutyTerritoryId == territoryId)
            return;

        ClearWeeklyLockoutDutyState();
    }

    private static void SyncWeeklyLockoutDutyState(uint territoryId)
    {
        if (!Config.WeeklyLockoutDutyActive)
            return;

        if (!IsHighEndDutyTerritory(territoryId) || Config.WeeklyLockoutDutyTerritoryId != territoryId)
            ClearWeeklyLockoutDutyState();
    }

    private static void ClearWeeklyLockoutDutyState()
    {
        if (Config is { WeeklyLockoutDutyActive: false, WeeklyLockoutDutyTerritoryId: 0 })
            return;

        Config.WeeklyLockoutDutyActive = false;
        Config.WeeklyLockoutDutyTerritoryId = 0;
        Config.Save();
        DuoLog.Debug("Weekly lockout duty suspension cleared.");
    }

    private static bool IsHighEndDutyTerritory(uint territoryId)
    {
        var territory = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
        var contentFinder = territory?.ContentFinderCondition.Value;
        return contentFinder is { HighEndDuty: true };
    }

    private static void TestWhatWouldLlDo(string idOrNameArg)
    {
        if (string.IsNullOrWhiteSpace(idOrNameArg))
        {
            DuoLog.Debug("Usage: /lazy test <Item ID or Item Name>");
            return;
        }

        var itemSheet = Svc.Data.GetExcelSheet<Item>();
        if (!uint.TryParse(idOrNameArg, out var itemId))
        {
            var search = idOrNameArg.Trim();
            var matches = itemSheet
                .Where(x => x.Name.ToString()
                    .Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

            switch (matches.Count)
            {
                case 0:
                    DuoLog.Debug($"No item found matching your search '{search}'.");
                    return;
                case > 1:
                    {
                        Svc.Chat.Print(new SeString(new List<Payload>
                    {
                        new TextPayload(
                            $"Found {matches.Count} entries for search '{search}'. Showing the first 5:")
                    }));

                        foreach (var match in matches.Take(5))
                        {
                            Svc.Chat.Print(new SeString(new List<Payload>
                            {
                                new TextPayload($"[LazyLoot Item Test] :: ID {match.RowId} :: "),
                                new UIForegroundPayload((ushort)(0x223 + match.Rarity * 2)),
                                new UIGlowPayload((ushort)(0x224 + match.Rarity * 2)),
                                new ItemPayload(match.RowId, true),
                                new TextPayload(match.Name.ExtractText()),
                                RawPayload.LinkTerminator,
                                new UIForegroundPayload(0),
                                new UIGlowPayload(0),
                            })
                            );
                        }

                        return;
                    }
                default:
                    itemId = matches[0].RowId;
                    break;
            }
        }

        if (itemId == 0)
        {
            DuoLog.Error($"Invalid item id or name: '{idOrNameArg}'.");
            return;
        }

        // itemId 可能是使用者直接打進來的任意數字（/lazy test 999999），
        // 裸 GetRow 查無此列時 Lumina 會擲例外並炸掉整個指令處理。
        // 走既有的「無效物品」錯誤分支就好。
        var itemRow = itemSheet.GetRowOrDefault(itemId);
        if (itemRow is null)
        {
            DuoLog.Error($"Invalid item id or name: '{idOrNameArg}'.");
            return;
        }

        var item = itemRow.Value;

        var tempDiagnosticsMode = Config.DiagnosticsMode;
        Config.DiagnosticsMode = true;
        Config.Save();
        var decision = Roller.WhatWouldLlDo(itemId);
        Config.DiagnosticsMode = tempDiagnosticsMode;
        Config.Save();

        var decisionText = decision switch
        {
            Roller.LlDecision.DoNothing => "DO NOTHING",
            Roller.LlDecision.Pass => "PASS",
            Roller.LlDecision.Greed => "GREED",
            Roller.LlDecision.Need => "NEED",
            _ => $"UNKNOWN ({decision})"
        };
        ushort decisionColor = decision switch
        {
            Roller.LlDecision.DoNothing => 8, // Grey
            Roller.LlDecision.Pass => 14, // Red
            Roller.LlDecision.Greed => 500, // Yellow
            Roller.LlDecision.Need => 45, // Green
            _ => 0
        };

        Svc.Chat.Print(new SeString(new List<Payload>
            {
                new TextPayload($"[LazyLoot Item Test] :: ID {itemId} :: "),
                new UIForegroundPayload((ushort)(0x223 + item.Rarity * 2)),
                new UIGlowPayload((ushort)(0x224 + item.Rarity * 2)),
                new ItemPayload(item.RowId, true),
                new TextPayload(item.Name.ExtractText()),
                RawPayload.LinkTerminator,
                new UIForegroundPayload(0),
                new UIGlowPayload(0),
                new TextPayload(" :: "),
                new UIForegroundPayload(decisionColor),
                new TextPayload($"{decisionText}"),
                new UIForegroundPayload(0),
            })
        );
    }
}