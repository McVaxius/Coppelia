using System;
using System.Linq;
using AethertekUI;
using AethertekUI.Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Coppelia.Windows;

namespace Coppelia;

public sealed partial class Plugin
{
    private MaterialGlassRenderer? backgroundGlass;
    internal bool BackgroundRequested => Configuration.UiStylingEnabled && Configuration.UiColors is { } choices &&
        (choices.HasBackgroundBlur(choices.Mode) || choices.Mode == MaterialColorMode.Glassmorphism && choices.GlassBackground == MaterialGlassBackground.Image);
    private readonly FileDialogManager glassImagePicker = new();

    private void DrawUi()
    {
        var active = false;
        var completed = false;
        try
        {
            ApplyAppearance();
            using (MaterialTheme.Push(uiTheme, ImGuiHelpers.GlobalScale, Configuration.UiStylingEnabled ? MaterialStyleMode.ColorsOnly : MaterialStyleMode.Native))
                glassImagePicker.Draw();
            var choices = Configuration.UiColors ?? new();
            var blur = Configuration.UiStylingEnabled && choices.HasBackgroundBlur(choices.Mode);
            var image = Configuration.UiStylingEnabled && choices.Mode == MaterialColorMode.Glassmorphism && choices.GlassBackground == MaterialGlassBackground.Image;
            active = blur || image;
            if (active) backgroundGlass ??= new(PluginInterface, Log);
            backgroundGlass?.ConfigureImage(TextureProvider, image, choices.GlassImagePath);
            backgroundGlass?.BeginFrame();
            using var background = MaterialSkin.PushBackgroundRenderer(Configuration.UiStylingEnabled ? backgroundGlass : null, blur, choices.BackgroundBlurPercent / 100f);
            DrawUiContent();
            completed = true;
        }
        finally { backgroundGlass?.EndFrame(active && completed); }
    }

    private void ReleaseGlassResources() => backgroundGlass?.ReleaseResources();
    private void ToggleUiStyling() { Configuration.UiStylingEnabled = !Configuration.UiStylingEnabled; Configuration.Save(); }
    private void ToggleWindowLock() { Configuration.UiWindowsLocked = !Configuration.UiWindowsLocked; Configuration.Save(); }

    internal void PrepareWindowBackground(string name)
    {
        if (backgroundGlass != null) backgroundGlass.Opacity = Configuration.UiTransparencyEnabled
            ? ImGui.IsWindowAppearing() ? Configuration.UiWindowOpacityPercent / 100f
                : windowOpacities.TryGetValue(name, out var opacity) ? opacity.CurrentOpacity : Configuration.UiWindowOpacityPercent / 100f
            : 1;
    }

    private void DrawUiWindowControls()
    {
        var styled = Configuration.UiStylingEnabled;
        if (UiGui.Checkbox("UI styling", ref styled)) { Configuration.UiStylingEnabled = styled; Configuration.Save(); }
        var locked = Configuration.UiWindowsLocked;
        if (UiGui.Checkbox("Lock window movement and resizing", ref locked)) { Configuration.UiWindowsLocked = locked; Configuration.Save(); }
    }

    private void BrowseBackgroundImage() => glassImagePicker.OpenFileDialog(
        UiText.T("Choose background image") + "###CoppeliaBackgroundImagePicker",
        UiText.T("Images") + "{.png,.jpg,.jpeg,.bmp,.gif,.webp,.tif,.tiff,.dds,.tga},.*",
        (accepted, path) =>
        {
            if (!accepted) return;
            colorDraft = colorDraft with { GlassImagePath = path };
            Configuration.UiColors = colorDraft;
            Configuration.Save();
        });

    private void DrawBackgroundSettings()
    {
        var choices = Configuration.UiColors ?? new();
        var blur = choices.HasBackgroundBlur(choices.Mode);
        var changed = UiGui.Checkbox("Background blur", ref blur);
        if (changed) choices = choices.WithBackgroundBlur(choices.Mode, blur);
        var frost = choices.BackgroundBlurPercent;
        MaterialText.Text(UiText.T("Blur (%)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        using (MaterialSkin.NativeFieldStyle())
            if (ImGui.InputInt("##BackgroundFrost", ref frost)) { choices = choices with { BackgroundBlurPercent = frost }; changed = true; }
        if (changed) { Configuration.UiColors = colorDraft = choices; Configuration.Save(); }
        DrawGlassStatusAndScreenshots();
    }

    private void DrawGlassStatusAndScreenshots()
    {
        if (colorDraft.Mode == MaterialColorMode.Glassmorphism && colorDraft.GlassBackground == MaterialGlassBackground.Image)
        {
            MaterialText.TextWrapped(UiText.T(backgroundGlass?.ImageProblem ?? (backgroundGlass?.ImageAvailable == true
                ? "Image backgrounds remain visible to screenshots." : string.IsNullOrWhiteSpace(colorDraft.GlassImagePath) ? "Choose an image file." : "Loading image...")));
            return;
        }
        var paused = backgroundGlass?.ScreenshotsPaused == true;
        ImGui.BeginDisabled(!paused && backgroundGlass?.CanPauseForScreenshots != true);
        if (UiGui.Checkbox("Pause backdrop for screenshots", ref paused)) backgroundGlass?.TrySetScreenshotsPaused(paused);
        ImGui.EndDisabled();
    }
}
