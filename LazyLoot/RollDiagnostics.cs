using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Text;

namespace LazyLoot;

/// <summary>
/// 純診斷模組：把遊戲的擲骰相關聊天訊息原樣寫進 log，用來判斷 ①「某某擲骰」是有人按下去就即時通報、
/// 還是開獎那一刻才一次印出來 ② 訊息裡的玩家名是不是 <see cref="PlayerPayload"/>。
/// 要使用者回報的診斷；預設關閉，由使用者自己打開（設定頁的「擲骰診斷記錄」或 <c>/lazy diag on</c>）；不做節流、不自動關閉。
/// </summary>
/// <remarks>
/// 記錄兩個 LogKind：<see cref="RollLogKind"/> 是擲骰結果訊息（值取自 LogMessage 表），<see cref="CastLotLogKind"/> 是校準錨
/// （「請擲骰。」，骰道具時必定出現）⇒ 一場副本的 log 裡 57 有輸出而 65 完全沒有＝表值與實際聊天型別不符；
/// 沒有這個錨的話，「零輸出」與「這場根本沒骰到東西」分不出來。
/// <para>
/// 刻意不掛在 <c>LazyLoot.NoticeLoot</c> 裡面：那個方法一開頭就是 <c>if (!Config.FulfEnabled) return;</c>，
/// 掛進去會讓診斷被 FULF 開關靜默吃掉。
/// </para>
/// <para>
/// 判別只用數值遮罩（LogKind = 低 7 位元），不比對任何翻譯過的訊息文字：台服的字串與國際服不同。
/// LogKind 57 是「系統訊息」大類，不只有擲骰提示：開著時實機一輪就是七萬多行 Information，
/// 這是為了拿到校準錨刻意付的代價。
/// </para>
/// </remarks>
internal static class RollDiagnostics
{
    /// <summary>log 前綴，之後 grep 用。</summary>
    private const string Prefix = "[RollDiag]";

    /// <summary>聊天型別的低 7 位元是 LogKind，其餘位元是來源／目標。</summary>
    private const int LogKindMask = 0x7F;

    /// <summary>
    /// 擲骰結果訊息的 LogKind：台服 LogMessage 表上只有「擲出 N 點」LM1231、「擲骰」LM5180 這兩列是 65。
    /// </summary>
    private const int RollLogKind = 65;

    /// <summary>
    /// 校準錨：「請擲骰。」LM5194 的 LogKind，已實測（原始 chat type 2105，2105 &amp; 0x7F = 57）。
    /// 骰道具時必定出現，用來把「65 猜錯」與「這場沒骰到東西」分開。
    /// </summary>
    private const int CastLotLogKind = 57;

    private static bool _subscribed;

    /// <summary>
    /// 依目前設定訂閱或取消訂閱。外掛啟動時與設定被切換時各呼叫一次。
    /// 🔴 關閉時「完全不訂閱」，而不是訂閱之後在回呼裡判斷 ——
    ///    這個回呼每一句聊天都會跑，留著訂閱等於白付成本。
    /// </summary>
    internal static void Refresh()
        => SetEnabled(LazyLoot.Config is { RollDiagnosticsLogging: true });

    internal static void SetEnabled(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }

    /// <summary>目前是否正在記錄。指令與 UI 用來回報狀態。</summary>
    internal static bool IsEnabled => _subscribed;

    private static void Enable()
    {
        if (_subscribed) return;

        Svc.Chat.ChatMessage += OnChatMessage;
        _subscribed = true;

        // Dalamud 的「Loading plugin」那行不帶版本號，所以在這裡自己印一次：
        // 之後看 log 時才分得出來讀到的是不是這一版的輸出。
        Svc.Log.Information(
            "{Message}",
            $"{Prefix} 擲骰聊天診斷已啟用（LogKind {RollLogKind}=擲骰結果、{CastLotLogKind}=校準錨）"
            + $" 版本={typeof(RollDiagnostics).Assembly.GetName().Version}");
    }

    internal static void Disable()
    {
        if (!_subscribed) return;

        Svc.Chat.ChatMessage -= OnChatMessage;
        _subscribed = false;
    }

    private static void OnChatMessage(
        XivChatType type,
        int timestamp,
        ref SeString sender,
        ref SeString message,
        ref bool isHandled)
    {
        var rawType = (int)type;
        var logKind = rawType & LogKindMask;
        if (logKind is not (RollLogKind or CastLotLogKind)) return;

        var senderCopy = sender;
        var messageCopy = message;

        try
        {
            // role 讓 grep 一眼看得出這行是目標還是校準錨，不必自己換算 logKind。
            var role = logKind == RollLogKind ? "roll" : "castLot(anchor)";

            var line =
                $"{Prefix} type={rawType} (0x{rawType:X4}) logKind={logKind} role={role}"
                + $" chatTs={timestamp}"
                + $" | sender.text='{Sanitize(senderCopy?.TextValue)}'"
                + $" sender.payloads={Describe(senderCopy)}"
                + $" | message.text='{Sanitize(messageCopy?.TextValue)}'"
                + $" message.payloads={Describe(messageCopy)}"
                + $" | {DescribeLoot()}";

            // 用參數帶入而不是直接把內插字串當樣板：聊天文字可能含大括號，
            // 直接當 Serilog 樣板會被解析成屬性欄位而變形。
            Svc.Log.Information("{Message}", line);
        }
        catch (Exception ex)
        {
            // 診斷本身壞掉也要看得見，否則會誤判成「這則訊息沒發生」。
            Svc.Log.Information(ex, "{Message}", $"{Prefix} 產生診斷輸出時發生例外（type={rawType}）");
        }
    }

    /// <summary>列出一段 SeString 的 payload 組成，重點在有沒有 PlayerPayload／ItemPayload。</summary>
    private static string Describe(SeString? seString)
    {
        if (seString == null) return "<null>";

        var payloads = seString.Payloads;
        if (payloads == null || payloads.Count == 0) return "<empty>";

        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < payloads.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(DescribePayload(payloads[i]));
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string DescribePayload(Payload? payload)
    {
        if (payload == null) return "<null>";

        try
        {
            switch (payload)
            {
                case PlayerPayload player:
                    {
                        var world = player.World.ValueNullable;
                        var worldName = world.HasValue ? world.Value.Name.ToString() : "?";
                        return $"Player(name='{Sanitize(player.PlayerName)}',"
                               + $" worldId={player.World.RowId}, world='{Sanitize(worldName)}')";
                    }

                case ItemPayload item:
                    return $"Item(id={item.ItemId}, rawId={item.RawItemId}, kind={item.Kind}, hq={item.IsHQ})";

                case TextPayload text:
                    return $"Text('{Sanitize(text.Text)}')";

                case RawPayload raw:
                    {
                        var data = raw.Data;
                        return data == null
                            ? "Raw(<null>)"
                            : $"Raw({Convert.ToHexString(data)})";
                    }

                default:
                    return payload.Type.ToString();
            }
        }
        catch (Exception ex)
        {
            // 單一 payload 描述失敗不該吃掉整行診斷。
            return $"{payload.GetType().Name}<描述失敗:{ex.GetType().Name}>";
        }
    }

    /// <summary>
    /// 訊息抵達當下的拾取清單狀態。
    /// 這一欄是回答問題①的關鍵：如果「某某擲骰」進來時清單裡還有未定案的道具，
    /// 就是即時通報；如果每次都是全部已定案之後才一次湧入，就是開獎才印。
    /// 🔴 只在這個呼叫裡讀，不跨幀保存任何原生指標。
    /// </summary>
    private static unsafe string DescribeLoot()
    {
        var loot = Loot.Instance();
        if (loot == null) return "loot=<null>";

        var span = loot->Items;

        var occupied = 0;
        var pending = 0;
        var slots = new StringBuilder();

        for (var i = 0; i < span.Length; i++)
        {
            // Span 索引取值會複製一份結構出來，之後不再碰原生記憶體。
            var item = span[i];

            // 與 Roller.GetNextLootItem 相同的空槽判定。
            if (item.ChestObjectId is 0 or 0xE0000000) continue;
            if (item.ItemId == 0) continue;

            occupied++;

            // Roller 也是這樣把 HQ 偏移還原成本體 id 的。
            var rawId = item.ItemId;
            var baseId = rawId >= 1000000 ? rawId - 1000000 : rawId;

            var isPending = item.RollResult == RollResult.UnAwarded
                            && item.RollState is not (RollState.Rolled
                                or RollState.Unavailable
                                or RollState.Unknown);
            if (isPending) pending++;

            if (slots.Length > 0) slots.Append(' ');
            slots.Append($"#{i}:item={baseId}{(rawId >= 1000000 ? "(HQ)" : string.Empty)}")
                 .Append($",state={item.RollState}({(byte)item.RollState})")
                 .Append($",result={item.RollResult}")
                 .Append($",rollValue={item.RollValue}")
                 .Append($",mode={item.LootMode}")
                 .Append($",weekly={item.WeeklyLootItem}")
                 .Append($",time={item.Time:0.0}/{item.MaxTime:0.0}")
                 .Append($",pending={isPending}");
        }

        return $"loot.occupied={occupied} loot.pending={pending} loot.slots=[{slots}]";
    }

    /// <summary>
    /// 把控制字元與私用區字元（自動翻譯括號、跨界圖示等）換成可見的 &lt;U+XXXX&gt;，
    /// 這樣每則診斷都保證是單獨一行、grep 撈得到，也不會漏掉分隔用的圖示字元。
    /// </summary>
    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (c < 0x20 || c == 0x7F || (c >= 0xE000 && c <= 0xF8FF))
            {
                sb.Append("<U+").Append(((int)c).ToString("X4")).Append('>');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
