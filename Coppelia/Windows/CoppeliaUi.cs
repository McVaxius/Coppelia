using System.Numerics;
using AethertekUI;
using Coppelia.Models;
using Dalamud.Bindings.ImGui;

namespace Coppelia.Windows;

internal static class CoppeliaUi
{
    public static Vector4 Accent => MaterialTheme.Current.Colors.Primary;
    public static Vector4 Ready => CoppeliaPresentation.Ready;
    public static Vector4 Warning => CoppeliaPresentation.Pending;
    public static Vector4 Blocked => CoppeliaPresentation.Error;
    public static Vector4 Muted => MaterialTheme.Current.Colors.OnSurfaceVariant;

    internal static void Panel(string id, uint root, Vector2 size, Action draw, bool raised = false, float? padding = null)
    {
        var scale = MaterialTheme.Metrics.Scale;
        var min = ImGui.GetCursorScreenPos();
        var extent = new Vector2(size.X <= 0 ? ImGui.GetContentRegionAvail().X : size.X, size.Y);
        CoppeliaPresentation.Surface(min, min + extent, raised);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2((padding ?? CoppeliaPresentation.PanelPadding) * scale));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (ImGui.BeginChild(id, size, false, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGuiP.PushOverrideID(root);
            draw();
            ImGui.PopID();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    internal static void Heading(string title, MaterialIcon icon)
    {
        using var font = UiText.Font(UiFontRole.Heading);
        using var typography = UiText.FontScale(16f / 15);
        var scale = MaterialTheme.Metrics.Scale;
        var min = ImGui.GetCursorScreenPos();
        MaterialIcons.Draw(icon, min, 22 * scale, Accent);
        ImGui.Dummy(new Vector2(22 * scale));
        ImGui.SameLine();
        UiGui.TextUnformatted(title);
        ImGui.Spacing();
    }

    internal static void Brand(string suffix = "")
    {
        var scale = MaterialTheme.Metrics.Scale;
        var min = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var ink = ImGui.GetColorU32(ImGuiCol.Text);
        var colors = MaterialTheme.Current.Colors;
        var mark = suffix == "Settings" ? 38 : suffix.Length == 0 && !CoppeliaPresentation.Compact ? 50 : 42;
        if (suffix.Length == 0)
            CoppeliaPresentation.DrawPluginIcon(dl, min, min + new Vector2(mark * scale));
        else
        {
            var center = min + new Vector2(mark * .5f) * scale;
            dl.AddRectFilled(min, min + new Vector2(mark) * scale, MaterialCanvas.Color(colors.PrimaryContainer), 4 * scale);
            dl.AddRect(min, min + new Vector2(mark) * scale, MaterialCanvas.Color(colors.Primary), 4 * scale);
            dl.AddRectFilled(center + new Vector2(-4, -12) * scale, center + new Vector2(4, 12) * scale, ink);
            dl.AddRectFilled(center + new Vector2(-12, -4) * scale, center + new Vector2(12, 4) * scale, ink);
        }
        ImGui.Dummy(new Vector2(mark * scale));
        ImGui.SameLine();
        ImGui.BeginGroup();
        var titleRole = suffix == "Settings" && CoppeliaPresentation.Compact ? UiFontRole.Heading : UiFontRole.Title;
        var titleScale = suffix switch
        {
            "" => CoppeliaPresentation.Compact ? .95f : 1.09f,
            "Mini" => .72f,
            "Watch" => .68f,
            _ => 1,
        };
        using (UiText.FontScale(titleScale))
        using (UiText.Font(titleRole))
            UiGui.TextUnformatted(suffix.Length == 0 ? "HealBot" : UiText.F("HealBot {0}", UiText.T(suffix)));
        if (suffix is "" or "Mini" or "Watch")
        {
            using var captionScale = UiText.FontScale(1.1f);
            using var caption = UiText.Font(UiFontRole.Caption);
            CoppeliaUi.WrappedHelp(suffix switch
            {
                "Mini" => "Compact companion for everyday play",
                "Watch" => "Manage watched targets and scan for allies.",
                _ => "Automated support for your adventures",
            });
        }
        ImGui.EndGroup();
    }

    internal static void IconSummary(MaterialIcon icon, Action draw, Vector4? color = null)
    {
        var scale = MaterialTheme.Metrics.Scale;
        var size = (CoppeliaPresentation.Compact ? 26 : 30) * scale;
        var min = ImGui.GetCursorScreenPos();
        ImGui.BeginGroup();
        MaterialIcons.Draw(icon, min + new Vector2(0, 3 * scale), size, color ?? MaterialTheme.Current.Colors.OnSurface);
        ImGui.Dummy(new Vector2(size, size + 3 * scale));
        ImGui.SameLine(0, (CoppeliaPresentation.Compact ? 12 : 16) * scale);
        ImGui.BeginGroup();
        draw();
        ImGui.EndGroup();
        ImGui.EndGroup();
    }

    // Native widgets retain their labels/IDs; translated sizes determine where a row wraps.
    internal static void SameLineFor(string label, float extra = 0)
    {
        var width = MaterialText.Measure(UiText.T(label.Split("##", 2)[0])).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + extra * MaterialTheme.Metrics.Scale;
        var x = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X;
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (x + width <= right) ImGui.SameLine();
    }

    public static void SectionHeader(string title, string? help = null)
    {
        ImGui.Spacing();
        using var font = UiText.Font(UiFontRole.BodyStrong);
        UiGui.TextColored(Accent, title);
        ImGui.Separator();
        if (!string.IsNullOrWhiteSpace(help))
            WrappedHelp(help);
    }

    public static void WrappedHelp(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        UiGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static void StatusLine(string label, bool ready, string readyText, string blockedText, bool optional = false)
    {
        var color = ready ? Ready : optional ? Warning : Blocked;
        var state = ready ? readyText : blockedText;
        UiGui.TextColored(color, UiText.F("{0}: {1}", UiText.T(label), UiText.T(state)));
    }

    public static void StatusText(string text, bool ready, bool warning = false)
    {
        var color = ready ? Ready : warning ? Warning : Blocked;
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        UiGui.TextWrapped(UiText.Status(text));
        ImGui.PopStyleColor();
    }

    internal static Vector4 OperationalColor(string state, OperatingRole role, string blocker)
    {
        var blocked = !string.IsNullOrWhiteSpace(blocker) || state.Contains("Blocked", StringComparison.OrdinalIgnoreCase);
        var pending = role == OperatingRole.Off || state.Contains("Waiting", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("Holding", StringComparison.OrdinalIgnoreCase) || state.Contains("Connecting", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("Authenticating", StringComparison.OrdinalIgnoreCase) || state.Contains("Listening", StringComparison.OrdinalIgnoreCase);
        return blocked ? Blocked : pending ? Warning : Ready;
    }

    internal static void OperationalState(string state, OperatingRole role, string blocker)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, OperationalColor(state, role, blocker));
        UiGui.TextWrapped(UiText.Status(state));
        ImGui.PopStyleColor();
    }

    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        var colors = MaterialTheme.Current.Colors;
        ImGui.PushStyleColor(ImGuiCol.Button, colors.PrimaryContainer);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Lerp(colors.PrimaryContainer, colors.Primary, .25f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Lerp(colors.PrimaryContainer, colors.Primary, .4f));
        var pressed = UiGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return pressed;
    }

    public static void Tooltip(string text, ImGuiHoveredFlags flags = ImGuiHoveredFlags.None)
    {
        if (ImGui.IsItemHovered(flags))
            UiGui.SetTooltip(text);
    }
}
