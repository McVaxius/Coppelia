using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;

namespace Coppelia.Windows;

internal enum UiFontRole { Body, BodyStrong, Title, Caption, Small, Heading, Action }

internal static class CoppeliaPresentation
{
    // Dalamud owns the shared texture through render submission; callers borrow its wrapper.
    internal static Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? OriginalIcon
        => Plugin.TextureProvider.GetFromManifestResource(typeof(Plugin).Assembly, "Coppelia.images.icon.png").GetWrapOrDefault();

    internal static void DrawPluginIcon(ImDrawListPtr drawList, Vector2 min, Vector2 max)
    {
        var texture = OriginalIcon;
        if (texture is not null)
            MaterialCanvas.DrawImage(drawList, texture.Handle, new Vector2(texture.Width, texture.Height), min, max);
    }

    // Approved HealBot-review-v3 / HealBot-compact-review-v1, logical pixels; retain native chrome.
    internal static bool Compact { get; set; }
    internal static float HeaderHeight => Compact ? 58 : 76;
    internal static float DashboardHeight => Compact ? 370 : 480;
    internal static float FooterHeight => Compact ? 220 : 266;
    internal static float PanelPadding => Compact ? 12 : 18;
    internal static float ActionHeight => Compact ? 30 : 36;
    internal static float Gap => Compact ? 12 : 16;
    internal const uint ReferenceAccent = 0x28E8B4;
    internal static readonly float[] FontSizes = [11, 12, 26, 10, 9, 16, 11];
    internal static readonly string[] FontFiles = ["segoeui.ttf", "seguisb.ttf", "segoeuib.ttf", "segoeui.ttf", "segoeui.ttf", "seguisb.ttf", "seguisb.ttf"];
    internal static float AtlasHeight(UiFontRole role) => FontSizes[(int)role] * 4 / 3;
    internal static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    internal static readonly Vector4 Ready = Rgb(0x4AF5BB), Pending = Rgb(0xE8B86C), Error = Rgb(0xEE7788);
    internal static MaterialControlMetrics Controls(float height, float icon = 20)
    {
        var s = MaterialTheme.Metrics.Scale;
        return new() { Height = height * s, Padding = new(12 * s, Math.Max(0, (height * s - ImGui.GetTextLineHeight()) * .5f)),
            Gap = 8 * s, IconSize = icon * s, Rounding = 4 * s, ItemSpacing = new(10 * s, 4 * s), CellPadding = new(16 * s, 8 * s) };
    }
    internal static MaterialTheme Theme(uint accent)
    {
        accent &= 0xFFFFFF;
        var reference = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new Vector3(Rgb(ReferenceAccent).X, Rgb(ReferenceAccent).Y, Rgb(ReferenceAccent).Z)));
        var selected = Rgb(accent);
        var seed = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(selected.X, selected.Y, selected.Z)));
        var hueShift = seed.Y < .001f ? 0 : seed.Z - reference.Z;
        var chromaScale = seed.Y < .001f ? 0 : seed.Y / reference.Y;
        Vector4 Relative(uint rgb)
        {
            var color = Rgb(rgb);
            if (accent == ReferenceAccent) return color;
            var lch = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(color.X, color.Y, color.Z)));
            return new(MaterialColor.GamutMap(lch.X, lch.Y * chromaScale, lch.Z + hueShift), 1);
        }
        var palette = new OklchPaletteGenerator().Generate(new(selected.X, selected.Y, selected.Z));
        var background = Relative(0x0D2023);
        var foreground = Relative(0xECF4F6);
        var primary = Relative(ReferenceAccent);
        var colors = new MaterialColorScheme(palette)
        {
            Background = background, OnBackground = foreground, Surface = Relative(0x0C1D1E), OnSurface = foreground,
            SurfaceContainerLowest = Relative(0x0C1C1E), SurfaceContainerLow = Relative(0x0C1D1E),
            SurfaceContainer = Relative(0x142330), SurfaceContainerHigh = Relative(0x1A3039), SurfaceContainerHighest = Relative(0x203941),
            SurfaceVariant = Relative(0x284A51), OnSurfaceVariant = Relative(0xB6CED2),
            Outline = Relative(0x3B5D64), OutlineVariant = Relative(0x284A51),
            Primary = primary, OnPrimary = MaterialColor.Contrast(primary, background) >= MaterialColor.Contrast(primary, foreground) ? background : foreground,
            PrimaryContainer = Relative(0x0B392E), OnPrimaryContainer = foreground,
            Secondary = Relative(0x9DDACF), OnSecondary = background, SecondaryContainer = Relative(0x1A3039), OnSecondaryContainer = foreground,
            Tertiary = Relative(0xA4D8E7), OnTertiary = background, TertiaryContainer = Relative(0x203941), OnTertiaryContainer = foreground,
            InverseSurface = foreground, InverseOnSurface = background, InversePrimary = Relative(0x147C61),
        };
        return new(colors) { SurfaceOpacity = 1 };
    }
    internal static void Surface(Vector2 min, Vector2 max, bool raised = false)
    {
        var c = MaterialTheme.Current.Colors;
        MaterialCanvas.Surface(min, max, raised ? c.SurfaceContainerHigh : c.Surface, c.Background, 4 * MaterialTheme.Metrics.Scale);
        Dalamud.Bindings.ImGui.ImGui.GetWindowDrawList().AddRect(min, max, MaterialCanvas.Color(c.OutlineVariant), 4 * MaterialTheme.Metrics.Scale);
    }
}
