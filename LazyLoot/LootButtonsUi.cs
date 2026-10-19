using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;

namespace LazyLoot;

/// <summary>
/// Need All / Greed All / Pass All bar drawn just outside the game's loot window (never over it).
/// Clicking only queues the same roll as /lazy need|greed|pass; the delays, restrictions and weekly-lockout pause all still apply.
/// The addon pointer is read and dropped inside one frame: only screen coordinates are kept.
/// </summary>
public unsafe class LootButtonsUi : Window, IDisposable
{
    private const string LootAddonName = "NeedGreed";

    private static readonly Vector4 ButtonColor = new(0.05f, 0.05f, 0.07f, 0.94f);
    private static readonly Vector4 ButtonHoveredColor = new(0.20f, 0.20f, 0.24f, 0.97f);
    private static readonly Vector4 ButtonActiveColor = new(0.32f, 0.32f, 0.38f, 1.00f);

    private readonly WindowSystem _windowSystem = new("LazyLootButtons");

    private Vector2 _position;
    private Vector2 _size;

    public LootButtonsUi() : base("###LazyLootLootButtons",
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking |
        ImGuiWindowFlags.NoBackground)
    {
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        DisableFadeInFadeOut = true;
        AllowPinning = false;
        AllowClickthrough = false;

        _windowSystem.AddWindow(this);
        Svc.PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
    }

    public void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        GC.SuppressFinalize(this);
    }

    public override bool DrawConditions()
    {
        if (!LazyLoot.Config.ShowLootButtons) return false;
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(LootAddonName, out var addon)) return false;
        if (!GenericHelpers.IsAddonReady(addon) || addon->RootNode == null) return false;

        // Field reads instead of the native GetScaledWidth/Height: nothing here can call a null function pointer.
        var width = addon->RootNode->Width * addon->Scale;
        var height = addon->RootNode->Height * addon->Scale;
        if (width <= 0 || height <= 0) return false;

        var barHeight = ImGui.GetFrameHeight();
        var viewport = ImGuiHelpers.MainViewport;
        var x = viewport.Pos.X + addon->X;
        var y = viewport.Pos.Y + addon->Y + height;

        // No room below: go above the window instead, still outside it.
        if (y + barHeight > viewport.Pos.Y + viewport.Size.Y)
            y = viewport.Pos.Y + addon->Y - barHeight;

        _position = new Vector2(x, y);
        _size = new Vector2(width, barHeight);
        return true;
    }

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.SetNextWindowPos(_position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(_size, ImGuiCond.Always);
        base.PreDraw();
    }

    public override void PostDraw()
    {
        base.PostDraw();
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonSize = new Vector2((_size.X - spacing * 2) / 3f, _size.Y);

        ImGui.PushStyleColor(ImGuiCol.Button, ButtonColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ButtonHoveredColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ButtonActiveColor);

        DrawRollButton("Need All".Loc() + "###LazyLootNeedAll", RollResult.Needed, buttonSize, "Same as /lazy need: roll need on everything, greed (or pass) where need is impossible. Your restrictions still apply.".Loc());
        ImGui.SameLine();
        DrawRollButton("Greed All".Loc() + "###LazyLootGreedAll", RollResult.Greeded, buttonSize, "Same as /lazy greed: roll greed on everything, pass where greed is impossible. Your restrictions still apply.".Loc());
        ImGui.SameLine();
        DrawRollButton("Pass All".Loc() + "###LazyLootPassAll", RollResult.Passed, buttonSize, "Same as /lazy pass: pass on everything you haven't rolled for yet.".Loc());

        ImGui.PopStyleColor(3);
    }

    private static void DrawRollButton(string label, RollResult result, Vector2 size, string tooltip)
    {
        if (ImGui.Button(label, size))
        {
            Svc.Log.Information($"[LazyLoot] Loot window button: {result}");
            LazyLoot.SetRollOption(result);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }
}
