using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AethertekUI;
using AethertekUI.Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace Coppelia.Windows;

internal sealed class CoppeliaFonts : IDisposable
{
    private static readonly UiFontRole[] WindowRoles =
    [
        UiFontRole.Body,
        UiFontRole.BodyStrong,
        UiFontRole.Title,
        UiFontRole.Caption,
        UiFontRole.Heading,
        UiFontRole.Action,
    ];

    private readonly IFontAtlas atlas;
    private readonly Dictionary<UiFontRole, IFontHandle> roles = [];
    private readonly List<IFontHandle> handles = [];
    private int generation;

    internal int Generation => System.Threading.Volatile.Read(ref generation);
    internal IEnumerable<UiFontRole> Roles => roles.Keys;
    internal MaterialTextRenderer? ShapedText { get; init; }

    internal CoppeliaFonts(IFontAtlas atlas, ushort[] ranges, string language, string assetRoot)
    {
        this.atlas = atlas;
        var faces = new Dictionary<(MaterialFontWeight Weight, float Height), IFontHandle>();
        atlas.BuildStepChange += MaterialFontAtlas.ReleaseRasterizedInputs;
        try
        {
            foreach (var role in WindowRoles)
            {
                var face = (CoppeliaPresentation.FontWeights[(int)role], CoppeliaPresentation.AtlasHeight(role));
                if (!faces.TryGetValue(face, out var handle))
                {
                    handle = MaterialFontAtlas.CreateFont(atlas, face.Item1, face.Item2, ranges, language, assetRoot,
                        pageSize: 2048, includeHostLanguage: false);
                    handles.Add(handle);
                    faces.Add(face, handle);
                    handle.ImFontChanged += FontChanged;
                }
                roles.Add(role, handle);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void FontChanged(IFontHandle handle, ILockedImFont font)
        => System.Threading.Interlocked.Increment(ref generation);

    internal bool Ready => handles.All(handle => handle.Available && handle.LoadException is null);
    internal Exception? LoadException => handles.FirstOrDefault(handle => handle.LoadException is not null)?.LoadException;

    internal unsafe void CheckGlyphs(IEnumerable<string> strings)
    {
        foreach (var (role, handle) in roles)
        {
            using var font = handle.Lock();
            ShapedText?.CheckGlyphs(strings, font.ImFont.FontSize);
            foreach (var text in strings)
                foreach (var rune in MaterialText.NativeGlyphText(text).EnumerateRunes().Where(r => !Rune.IsControl(r)))
                    if (rune.Value > ushort.MaxValue || ImGui.FindGlyphNoFallback(font.ImFont, (ushort)rune.Value).Handle == null)
                        throw new InvalidOperationException($"Required UI glyph missing: U+{rune.Value:X4} in {role}");
        }
    }

    internal IDisposable Push(UiFontRole role)
    {
        var handle = roles[role];
        if (!handle.Available || handle.LoadException is not null)
            throw new InvalidOperationException("Coppelia fonts are not ready.", handle.LoadException);
        return handle.Push();
    }

    public void Dispose()
    {
        atlas.BuildStepChange -= MaterialFontAtlas.ReleaseRasterizedInputs;
        foreach (var handle in handles)
        {
            handle.ImFontChanged -= FontChanged;
            handle.Dispose();
        }
        atlas.Dispose();
    }
}
