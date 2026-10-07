using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using AethertekUI;
using Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace Coppelia.Windows;

internal sealed class CoppeliaFonts : IDisposable
{
    private readonly IFontHandle[] handles;
    private int generation;
    internal int Generation => System.Threading.Volatile.Read(ref generation);
    internal MaterialTextRenderer? ShapedText { get; set; }
    internal CoppeliaFonts(IFontAtlas atlas, ushort[] ranges,string language)
    {
        handles=CoppeliaPresentation.FontSizes.Select((size,index)=>atlas.NewDelegateFontHandle(toolkit=>toolkit.OnPreBuild(build=>
        {
            build.NewImAtlas.TexDesiredWidth=4096;
            build.NewImAtlas.TexDesiredHeight=4096;
            size=CoppeliaPresentation.AtlasHeight((UiFontRole)index);
            var config=new SafeFontConfig { SizePx=size, GlyphRanges=ranges };
            build.Font=build.AddFontFromFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),CoppeliaPresentation.FontFiles[index]),config);
            build.AddFontFromFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"seguisym.ttf"),
                new SafeFontConfig { SizePx=size, MergeFont=build.Font, GlyphRanges=ranges });
            // The language selector always displays every native name. Host-managed merges cover these too.
            build.AddDalamudAssetFont(DalamudAsset.NotoSansCjkRegular,new SafeFontConfig
            {
                SizePx=size, MergeFont=build.Font, GlyphRanges=ranges,
                // Verified bundled TTC faces: JP=0, KR=1, SC=2, TC=3.
                // Bundled faces share glyph coverage; select the active locale's regional forms.
                FontNo=language switch { "ko"=>1, "zh-Hans" or "zh-CN"=>2, "zh-Hant" or "zh-TW"=>3, _=>0 },
            });
            build.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx=size, MergeFont=build.Font });
            build.AddGameSymbol(new SafeFontConfig { SizePx=size,MergeFont=build.Font });
        }))).ToArray();
        foreach(var handle in handles) handle.ImFontChanged+=FontChanged;
    }
    private void FontChanged(IFontHandle handle,ILockedImFont font) => System.Threading.Interlocked.Increment(ref generation);
    internal bool Ready => handles.All(h=>h.Available && h.LoadException is null);
    internal Exception? LoadException => handles.FirstOrDefault(h=>h.LoadException is not null)?.LoadException;
    internal unsafe void CheckGlyphs(IEnumerable<string> strings)
    {
        for(var index=0; index<handles.Length; index++)
        {
            using var font=handles[index].Lock();
            ShapedText?.CheckGlyphs(strings,font.ImFont.FontSize);
            foreach(var text in strings)
                foreach(var rune in MaterialText.NativeGlyphText(text).EnumerateRunes().Where(r=>!Rune.IsControl(r)))
                    if(rune.Value>ushort.MaxValue || ImGui.FindGlyphNoFallback(font.ImFont,(ushort)rune.Value).Handle==null)
                        throw new InvalidOperationException("Required UI glyph missing: U+"+rune.Value.ToString("X4")+" in "+(UiFontRole)index);
        }
    }
    internal IDisposable Push(UiFontRole role)
    {
        var handle=handles[(int)role];
        if(!handle.Available || handle.LoadException is not null) throw new InvalidOperationException("HealBot fonts are not ready.",handle.LoadException);
        return handle.Push();
    }
    public void Dispose() { foreach(var handle in handles) { handle.ImFontChanged-=FontChanged;handle.Dispose(); } }
}
