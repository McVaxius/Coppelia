using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Coppelia.Windows;

internal static class CoppeliaUi
{
    public static readonly Vector4 Accent = new(0.55f, 0.72f, 1.00f, 1.00f);
    public static readonly Vector4 Ready = new(0.42f, 1.00f, 0.56f, 1.00f);
    public static readonly Vector4 Warning = new(0.95f, 0.72f, 0.30f, 1.00f);
    public static readonly Vector4 Blocked = new(1.00f, 0.55f, 0.55f, 1.00f);
    public static readonly Vector4 Muted = new(0.68f, 0.72f, 0.80f, 1.00f);

    public static void SectionHeader(string title, string? help = null)
    {
        ImGui.Spacing();
        ImGui.TextColored(Accent, title);
        ImGui.Separator();
        if (!string.IsNullOrWhiteSpace(help))
            WrappedHelp(help);
    }

    public static void WrappedHelp(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static void StatusLine(string label, bool ready, string readyText, string blockedText, bool optional = false)
    {
        var color = ready ? Ready : optional ? Warning : Blocked;
        var state = ready ? readyText : blockedText;
        ImGui.TextColored(color, $"{label}: {state}");
    }

    public static void StatusText(string text, bool ready, bool warning = false)
    {
        var color = ready ? Ready : warning ? Warning : Blocked;
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.22f, 0.42f, 0.72f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.52f, 0.86f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.36f, 0.64f, 1.00f));
        var pressed = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return pressed;
    }

    public static void Tooltip(string text, ImGuiHoveredFlags flags = ImGuiHoveredFlags.None)
    {
        if (ImGui.IsItemHovered(flags))
            ImGui.SetTooltip(text);
    }
}
