using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Coppelia.Windows;

// Keep the ORIGINAL label passed to native ImGui. Translated ink is painted into the native item's measured bounds.
// This retains English-derived IDs, popup identity, selection, editing, focus and navigation behavior.
internal static class UiGui
{
    internal static void Text(string text) => MaterialText.Text(UiText.T(text));
    internal static void Text(FormattableString text) => MaterialText.Text(UiText.F(text));
    internal static void TextUnformatted(string text) => MaterialText.Text(UiText.T(text));
    internal static void TextWrapped(string text) => MaterialText.TextWrapped(UiText.T(text));
    internal static void TextDisabled(string text) { ImGui.PushTextWrapPos(0);MaterialText.TextColored(ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],UiText.T(text));ImGui.PopTextWrapPos(); }
    internal static void TextColored(Vector4 color,string text) { ImGui.PushTextWrapPos(0);MaterialText.TextColored(color,UiText.T(text));ImGui.PopTextWrapPos(); }
    internal static void BulletText(string text) => MaterialText.BulletText(UiText.T(text));
    internal static void SetTooltip(string text) { ImGui.BeginTooltip();ImGui.PushTextWrapPos(Math.Min(560*MaterialTheme.Metrics.Scale,ImGui.GetMainViewport().WorkSize.X*.8f));MaterialText.Text(UiText.T(text));ImGui.PopTextWrapPos();ImGui.EndTooltip(); }
    private static string Visible(string label) => UiText.T(label.Split("##",2)[0]);
    private static void Ink(string label,Vector2 position,Vector2 min,Vector2 max)
    {
        var dl=ImGui.GetWindowDrawList();dl.PushClipRect(min,max,true);
        MaterialText.AddText(dl,position,ImGui.GetColorU32(ImGuiCol.Text),label);dl.PopClipRect();
    }
    internal static bool Button(string original,Vector2 size=default,string? display=null)
    {
        var translated=display ?? Visible(original);
        using var height=MaterialText.PushLineHeight(translated);
        var natural=MaterialText.Measure(translated).X+ImGui.GetStyle().FramePadding.X*2;
        size.X=MaterialLayout.FitNextItemWidth(size.X,Math.Max(size.X,natural));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);
        var clicked=ImGui.Button(original,size);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();
        Ink(translated,min+(max-min-MaterialText.Measure(translated))*.5f,min,max);
        if (MaterialText.Measure(translated).X>max.X-min.X-ImGui.GetStyle().FramePadding.X*2 && ImGui.IsItemHovered()) SetTooltip(translated);
        return clicked;
    }
    internal static bool SmallButton(string label)
    { ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,new Vector2(ImGui.GetStyle().FramePadding.X,0));var click=Button(label);ImGui.PopStyleVar();return click; }
    internal static bool Checkbox(string label,ref bool value,string? display=null)
    {
        var translated=UiText.T(display ?? label.Split("##",2)[0]);var padding=ImGui.GetStyle().FramePadding;var gap=ImGui.GetStyle().ItemInnerSpacing;
        using var height=MaterialText.PushLineHeight(translated);
        var original=label.Split("##",2)[0];
        var textSize=MaterialText.Measure(translated);
        MaterialLayout.FitNextItemWidth(0,ImGui.GetFrameHeight()+gap.X+textSize.X);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing,new Vector2(Math.Max(0,gap.X+textSize.X-ImGui.CalcTextSize(original).X),gap.Y));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var changed=ImGui.Checkbox(label,ref value);ImGui.PopStyleColor();ImGui.PopStyleVar();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();
        var p=min+new Vector2(ImGui.GetFrameHeight()+gap.X,padding.Y);
        var dl=ImGui.GetWindowDrawList();MaterialText.AddText(dl,p,ImGui.GetColorU32(ImGuiCol.Text),translated);
        return changed;
    }
    internal static bool RadioButton(string label,bool selected,string? display=null)
    {
        var translated=UiText.T(display ?? label.Split("##",2)[0]);var original=label.Split("##",2)[0];var gap=ImGui.GetStyle().ItemInnerSpacing;
        using var lineHeight=MaterialText.PushLineHeight(translated);
        MaterialLayout.FitNextItemWidth(0,ImGui.GetFrameHeight()+gap.X+MaterialText.Measure(translated).X);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing,new Vector2(Math.Max(0,gap.X+MaterialText.Measure(translated).X-ImGui.CalcTextSize(original).X),gap.Y));
        var height=ImGui.GetFrameHeight();
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,Vector4.Zero);ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,Vector4.Zero);ImGui.PushStyleColor(ImGuiCol.FrameBgActive,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.CheckMark,Vector4.Zero);ImGui.PushStyleColor(ImGuiCol.Border,Vector4.Zero);
        var changed=ImGui.RadioButton(label,selected);ImGui.PopStyleColor(6);ImGui.PopStyleVar();
        var min=ImGui.GetItemRectMin();var p=min+new Vector2(height+gap.X,(ImGui.GetItemRectSize().Y-MaterialText.Measure(translated).Y)*.5f);
        var scale=MaterialTheme.Metrics.Scale;var center=min+new Vector2(height*.5f);var colors=MaterialTheme.Current.Colors;
        var circle=selected?colors.Primary:colors.OnSurface;circle.W*=ImGui.GetStyle().Alpha;
        ImGui.GetWindowDrawList().AddCircle(center,8*scale,MaterialCanvas.Color(circle),24,1.5f*scale);
        if(selected)ImGui.GetWindowDrawList().AddCircleFilled(center,4*scale,MaterialCanvas.Color(circle),24);
        // Native radio captions use the window clip: shaped glyph ink can extend
        // past its logical advance without changing the original hit rectangle.
        MaterialText.AddText(ImGui.GetWindowDrawList(),p,ImGui.GetColorU32(ImGuiCol.Text),translated);return changed;
    }
    internal static bool RadioTile(string label,bool selected,float width=0,string? display=null,float height=0)
    {
        var scale=MaterialTheme.Metrics.Scale;
        var text=UiText.T(display ?? label.Split("##",2)[0]);
        using var lineHeight=MaterialText.PushLineHeight(text);
        height=(height>0?height:CoppeliaPresentation.ActionHeight)*scale;
        height=Math.Max(height,MaterialText.Measure(text).Y+12*scale);
        width=Math.Max(width,MaterialText.Measure(text).X+height+18*scale);
        width=MaterialLayout.FitNextItemWidth(width,width);
        var min=ImGui.GetCursorScreenPos();
        var colors=MaterialTheme.Current.Colors;
        var dl=ImGui.GetWindowDrawList();
        dl.AddRectFilled(min,min+new Vector2(width,height),MaterialCanvas.Color(selected?colors.PrimaryContainer:colors.SurfaceContainer),4*scale);
        dl.AddRect(min,min+new Vector2(width,height),MaterialCanvas.Color(selected?colors.Primary:colors.OutlineVariant),4*scale);
        var raw=label.Split("##",2)[0];
        var gap=ImGui.GetStyle().ItemInnerSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,new Vector2(6*scale,Math.Max(0,(height-ImGui.GetTextLineHeight())*.5f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing,new Vector2(width-height-ImGui.CalcTextSize(raw).X,gap.Y));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.CheckMark,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border,Vector4.Zero);
        var changed=ImGui.RadioButton(label,selected);
        ImGui.PopStyleColor(6);ImGui.PopStyleVar(2);
        var center=min+new Vector2(height*.5f,height*.5f);
        dl.AddCircle(center,8*scale,MaterialCanvas.Color(selected?colors.Primary:colors.OnSurface),24,1.5f*scale);
        if(selected)dl.AddCircleFilled(center,4*scale,MaterialCanvas.Color(colors.Primary),24);
        Ink(text,min+new Vector2(height,(height-ImGui.GetTextLineHeight())*.5f),min,min+new Vector2(width,height));
        return changed;
    }
    internal static bool CollapsingHeader(string label,ImGuiTreeNodeFlags flags=ImGuiTreeNodeFlags.None)
    {
        using var height=MaterialText.PushLineHeight(Visible(label));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var open=ImGui.CollapsingHeader(label,flags);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();var padding=ImGui.GetStyle().FramePadding;
        MaterialIcons.Draw(open?MaterialIcon.ChevronDown:MaterialIcon.ArrowRight,min+padding,ImGui.GetFontSize(),MaterialTheme.Current.Colors.OnSurface,ImGui.GetStyle().Alpha);
        Ink(Visible(label),min+new Vector2(ImGui.GetFontSize()+padding.X*2,padding.Y),min,max);return open;
    }
    internal static bool TreeNode(string label)
    {
        using var height=MaterialText.PushLineHeight(Visible(label));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var open=ImGui.TreeNode(label);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();
        MaterialIcons.Draw(open?MaterialIcon.ChevronDown:MaterialIcon.ArrowRight,min,ImGui.GetFontSize(),MaterialTheme.Current.Colors.OnSurface,ImGui.GetStyle().Alpha);
        var p=min+new Vector2(ImGui.GetFontSize()+ImGui.GetStyle().FramePadding.X*2,0);
        Ink(Visible(label),p,min,new Vector2(p.X+MaterialText.Measure(Visible(label)).X,max.Y));return open;
    }
    internal static bool BeginTabItem(string label,ImGuiTabItemFlags flags=ImGuiTabItemFlags.None)
    {
        var translated=Visible(label);
        using var height=MaterialText.PushLineHeight(translated);
        var owner=ImGui.GetCurrentContext().CurrentTabBar;
        using var barMetrics=new MaterialStyleScope();
        if(!owner.IsNull)barMetrics.Style(ImGuiStyleVar.FramePadding,owner.FramePadding);
        ImGui.SetNextItemWidth(MaterialText.Measure(translated).X+ImGui.GetStyle().FramePadding.X*2+12*MaterialTheme.Metrics.Scale);
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var open=ImGui.BeginTabItem(label,flags);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();Ink(translated,min+(max-min-MaterialText.Measure(translated))*.5f,min,max);return open;
    }
    internal static bool BeginPrimaryTabItem(string label,ImGuiTabItemFlags flags,MaterialIcon icon,float width)
    {
        var scale=MaterialTheme.Metrics.Scale;using var font=UiText.Font(UiFontRole.BodyStrong);
        var height=(CoppeliaPresentation.Compact?40:44)*scale;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,new Vector2(12*scale,Math.Max(0,(height-ImGui.GetTextLineHeight())*.5f)));
        ImGui.SetNextItemWidth(width);
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var open=ImGui.BeginTabItem(label,flags);ImGui.PopStyleColor();ImGui.PopStyleVar();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();var text=Visible(label);var textSize=MaterialText.Measure(text);
        var p=min+new Vector2(Math.Max(4*scale,(max.X-min.X-textSize.X-30*scale)*.5f),(max.Y-min.Y-24*scale)*.5f);
        var color=MaterialTheme.Current.Colors.OnSurface;
        var dl=ImGui.GetWindowDrawList();dl.PushClipRect(min,max,true);MaterialIcons.Draw(icon,p,24*scale,color);
        MaterialText.AddText(dl,p+new Vector2(32*scale,(24*scale-textSize.Y)*.5f),ImGui.GetColorU32(ImGuiCol.Text),text);dl.PopClipRect();
        if (textSize.X+30*scale>max.X-min.X && ImGui.IsItemHovered()) SetTooltip(text);
        return open;
    }

    internal static bool BeginTabBar(string id,string[] captions)
    {
        using var height=MaterialText.PushLineHeight(captions.Select(UiText.T).ToArray());
        return ImGui.BeginTabBar(id);
    }

    internal static bool BeginCombo(string label,string preview,ImGuiComboFlags flags=ImGuiComboFlags.None,bool translatePreview=true)
        => BeginComboCore(label,preview,flags,translatePreview,0);
    private static bool BeginComboCore(string label,string preview,ImGuiComboFlags flags,bool translatePreview,float minimum)
    {
        var shown=translatePreview?UiText.T(preview):preview;
        using var height=MaterialText.PushLineHeight(shown,Visible(label));
        minimum=Math.Max(minimum,Math.Max(80*MaterialTheme.Metrics.Scale,MaterialText.Measure(shown).X+ImGui.GetFrameHeight()+2*ImGui.GetStyle().FramePadding.X));
        var field=FitField(label,minimum);var open=MaterialText.BeginCombo(label,shown,flags);FieldLabel(field);
        if(!open && MaterialText.Measure(shown).X>field.Width-ImGui.GetFrameHeight() && ImGui.IsItemHovered()) SetTooltip(shown);
        return open;
    }
    internal static bool Selectable(string label,bool selected=false,ImGuiSelectableFlags flags=ImGuiSelectableFlags.None,Vector2 size=default)
    {
        var origin=ImGui.GetCursorScreenPos();
        using var height=MaterialText.PushLineHeight(Visible(label));
        size=Vector2.Max(size,MaterialText.Measure(Visible(label)));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var changed=ImGui.Selectable(label,selected,flags,size);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();Ink(Visible(label),origin,min,max);
        if (MaterialText.Measure(Visible(label)).X>max.X-min.X && ImGui.IsItemHovered()) SetTooltip(Visible(label));return changed;
    }
    private static (Vector2 Min,Vector2 PreviousMax,float Width,string Label,ImGuiWindowPtr Window,ImDrawListPtr Drawing) FitField(string label,float minimum,bool showLabel=true,float? preferred=null)
    {
        var requested=preferred ?? ImGui.CalcItemWidth();
        var translated=showLabel?Visible(label):"";
        var gap=translated.Length>0?ImGui.GetStyle().ItemInnerSpacing.X:0;
        var labelWidth=MaterialText.Measure(translated).X;minimum=MathF.Ceiling(minimum);
        MaterialLayout.FitNextItemWidth(requested+labelWidth+gap,minimum+labelWidth+gap);
        var available=ImGui.GetContentRegionAvail().X;
        if(labelWidth+gap+minimum>available && translated.Length>0)
        {
            MaterialText.Text(translated);translated="";labelWidth=0;gap=0;
            available=ImGui.GetContentRegionAvail().X;
        }
        var width=MaterialLayout.FitNextItemWidth(Math.Min(requested,available-labelWidth-gap),minimum);
        ImGui.SetNextItemWidth(width);var min=ImGui.GetCursorScreenPos();var parent=ImGuiP.GetCurrentWindow();var drawing=ImGui.GetWindowDrawList();
        var previousMax=parent.DC.CursorMaxPos;
        // Native IDs and field contents remain exact; the translated label owns its visible layout.
        var window=ImGui.GetWindowPos();
        drawing.PushClipRect(new Vector2(min.X,window.Y),new Vector2(min.X+width,window.Y+ImGui.GetWindowSize().Y),true);
        return (min,previousMax,width,translated,parent,drawing);
    }
    private static void FieldLabel((Vector2 Min,Vector2 PreviousMax,float Width,string Label,ImGuiWindowPtr Window,ImDrawListPtr Drawing) field)
    {
        field.Drawing.PopClipRect();var right=field.Min.X+field.Width;
        if(field.Label.Length>0)
        {
            var p=new Vector2(right+ImGui.GetStyle().ItemInnerSpacing.X,field.Min.Y+(ImGui.GetFrameHeight()-MaterialText.Measure(field.Label).Y)*.5f);
            MaterialText.AddText(field.Drawing,p,ImGui.GetColorU32(ImGuiCol.Text),field.Label);right=p.X+MaterialText.Measure(field.Label).X;
        }
        field.Window.DC.CursorMaxPos=new Vector2(Math.Max(field.PreviousMax.X,right),field.Window.DC.CursorMaxPos.Y);
        field.Window.DC.CursorPosPrevLine=new Vector2(right,field.Window.DC.CursorPosPrevLine.Y);
    }
    internal static bool InputText(string label,ref string value,int length,ImGuiInputTextFlags flags=ImGuiInputTextFlags.None,bool showLabel=true)
    {
        using var height=MaterialText.PushLineHeight(value,showLabel?Visible(label):"");
        var field=FitField(label,TextMinimum(),showLabel);
        var changed=MaterialShapedInput.SingleLine(label,"",ref value,length,flags);FieldLabel(field);
        return changed;
    }
    internal static bool InputTextWithHint(string label,string hint,ref string value,int length,ImGuiInputTextFlags flags=ImGuiInputTextFlags.None)
    { using var height=MaterialText.PushLineHeight(value,UiText.T(hint),Visible(label));var field=FitField(label,TextMinimum());var changed=MaterialShapedInput.SingleLine(label,UiText.T(hint),ref value,length,flags);FieldLabel(field);return changed; }
    internal static bool InputTextMultiline(string label,ref string value,int length,Vector2 size,ImGuiInputTextFlags flags=ImGuiInputTextFlags.None)
    { using var height=MaterialText.PushLineHeight(value,Visible(label));var field=FitField(label,TextMinimum(),preferred:size.X>0?size.X:ImGui.CalcItemWidth());size.X=field.Width;var changed=MaterialShapedInput.Multiline(label,ref value,length,size,flags);FieldLabel(field);return changed; }
    internal static bool InputInt(string label,ref int value,int step=0,int fastStep=0,ImGuiInputTextFlags flags=ImGuiInputTextFlags.None)
    { using var height=MaterialText.PushLineHeight(Visible(label));var field=FitField(label,NumberMinimum(step>0));var changed=ImGui.InputInt(label,ref value,step,fastStep,"%d",flags);FieldLabel(field);return changed; }
    internal static bool SliderInt(string label,ref int value,int min,int max,string format="%d",ImGuiSliderFlags flags=ImGuiSliderFlags.None)
    { using var height=MaterialText.PushLineHeight(Visible(label));var field=FitField(label,NumberMinimum(false));var changed=ImGui.SliderInt(label,ref value,min,max,UiText.T(format),flags);FieldLabel(field);return changed; }
    private static float TextMinimum() => Math.Max(80*MaterialTheme.Metrics.Scale,MaterialText.Measure("0000000000").X+2*ImGui.GetStyle().FramePadding.X);
    internal static float NumberMinimum(bool steps) => MathF.Ceiling(Math.Max(80*MaterialTheme.Metrics.Scale,MaterialText.Measure("-000000").X+2*ImGui.GetStyle().FramePadding.X))+(steps?2*(ImGui.GetFrameHeight()+ImGui.GetStyle().ItemInnerSpacing.X):0);
    internal static bool Combo(string label,ref int value,string[] options,int count)
    {
        using var height=MaterialText.PushLineHeight(options.Take(count).Select(UiText.T).Append(Visible(label)).ToArray());
        var changed=false;
        var minimum=options.Take(count).Select(option=>MaterialText.Measure(UiText.T(option)).X).DefaultIfEmpty(0).Max()+ImGui.GetFrameHeight()+2*ImGui.GetStyle().FramePadding.X;
        if (BeginComboCore(label,value>=0 && value<count?options[value]:string.Empty,ImGuiComboFlags.None,true,minimum))
        {
            for(var index=0;index<count;index++)
            { ImGui.PushID(index);if (Selectable(options[index],value==index)) { changed=value!=index;value=index; }if (value==index) ImGui.SetItemDefaultFocus();ImGui.PopID(); }
            ImGui.EndCombo();
        }
        return changed;
    }
    internal static bool Combo(string label,ref int value,string options)
    { var items=options.Split('\0').Where(v=>v.Length>0).ToArray();return Combo(label,ref value,items,items.Length); }
    internal static bool BeginPopupModal(string original,ImGuiWindowFlags flags)
    {
        ImGui.SetNextWindowSize(new Vector2(520*MaterialTheme.Metrics.Scale,0),ImGuiCond.Always);
        var open=ImGui.BeginPopupModal(original,flags);if(open) Title(original.Split("##",2)[0]);return open;
    }
    internal static void Title(string original,string? display=null)
        => TitleWithButtons(original, display, null);

    internal static void ReserveTitleSpace(Window owner, string visible, float minimumWidth)
    {
        var style = ImGui.GetStyle();
        var fontSize = ImGui.GetFontSize();
        var collapse = (owner.Flags & (ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.Modal)) == 0
            && style.WindowMenuButtonPosition != ImGuiDir.None;
        var controls = AdditionalTitleButtonWidth(owner, fontSize)
            + ((owner.ShowCloseButton ? 1 : 0) + (collapse ? 1 : 0)) * (fontSize + style.ItemInnerSpacing.X);
        var required = (MaterialText.Measure(visible).X + controls + style.FramePadding.X * 2 + style.ItemInnerSpacing.X)
            / ImGui.GetIO().FontGlobalScale;
        var bounds = owner.SizeConstraints ?? new WindowSizeConstraints();
        bounds.MinimumSize = new(Math.Max(minimumWidth, required), bounds.MinimumSize.Y);
        owner.SizeConstraints = bounds;
    }

    private static float AdditionalTitleButtonWidth(Window? owner, float fontSize)
    {
        if (owner is null) return 0;
        var count = owner.TitleBarButtons.Count(button => !owner.IsClickthrough || button.AvailableClickthrough);
        if (owner.AllowPinning || owner.AllowClickthrough || owner.AllowBackgroundBlur) count++;
        return count * (fontSize + ImGui.GetStyle().ItemInnerSpacing.X);
    }

    internal static void TitleWithButtons(string original,string? display, Window? owner)
    {
        var translated=display ?? UiText.T(original);if (translated==original) return;
        if(MaterialText.RequiresShaping(translated))return; // Painted after native End, including collapsed owners.
        var style=ImGui.GetStyle();var fontSize=ImGui.GetFontSize();var height=ImGui.GetFrameHeight();
        var flags=ImGuiP.GetCurrentWindow().Flags;
        var collapseLeft=(flags & (ImGuiWindowFlags.NoCollapse|ImGuiWindowFlags.Modal))==0 && style.WindowMenuButtonPosition==ImGuiDir.Left;
        var p=ImGui.GetWindowPos()+new Vector2(style.FramePadding.X+(collapseLeft?fontSize+style.ItemInnerSpacing.X:0),style.FramePadding.Y);
        var reserved = owner is null ? 2 * height
            : style.FramePadding.X * 2 + (owner.ShowCloseButton ? fontSize : 0) + AdditionalTitleButtonWidth(owner, fontSize);
        if (owner is not null && (flags & ImGuiWindowFlags.NoCollapse) == 0 && style.WindowMenuButtonPosition == ImGuiDir.Right)
            reserved += fontSize + style.ItemInnerSpacing.X;
        var dl=ImGui.GetWindowDrawList();dl.PushClipRect(owner is null ? ImGui.GetWindowPos() : p,ImGui.GetWindowPos()+new Vector2(Math.Max(0,ImGui.GetWindowSize().X-reserved),height),false);
        var background=style.Colors[(int)(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)?ImGuiCol.TitleBgActive:ImGuiCol.TitleBg)];
        dl.AddRectFilled(p,p+new Vector2(Math.Max(ImGui.CalcTextSize(original).X,MaterialText.Measure(translated).X),height-style.FramePadding.Y),ImGui.ColorConvertFloat4ToU32(background));
        MaterialText.AddText(dl,p,ImGui.GetColorU32(ImGuiCol.Text),translated);dl.PopClipRect();
    }

    internal static void TableHeadersRow()
    {
        var captions=Enumerable.Range(0,ImGui.TableGetColumnCount()).Select(index=>UiText.T(ImGui.TableGetColumnName(index))).ToArray();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers,captions.Any(MaterialText.RequiresShaping)?captions.Select(text=>MaterialText.Measure(text).Y).Max():0);
        for(var index=0;index<ImGui.TableGetColumnCount();index++)
        {
            if(!ImGui.TableSetColumnIndex(index)) continue;
            TableHeader(ImGui.TableGetColumnName(index));
        }
    }
    internal static void TableHeader(string original)
    {
        var translated=UiText.T(original);
        using var height=MaterialText.PushLineHeight(translated);
        var p=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);ImGui.TableHeader(original);ImGui.PopStyleColor();
        MaterialText.AddText(ImGui.GetWindowDrawList(),p,ImGui.GetColorU32(ImGuiCol.Text),translated);
        if(MaterialText.Measure(translated).X>width && ImGui.IsItemHovered()) SetTooltip(translated);
    }
    internal static unsafe void PaintWindowTitle(string name,string display,MaterialTextRenderer renderer)
        => PaintWindowTitleWithButtons(name, display, renderer, null);

    internal static unsafe void PaintWindowTitleWithButtons(string name,string display,MaterialTextRenderer renderer, Window? owner)
    {
        var text=display; var original=name.Split("##",2)[0];
        if (!MaterialText.RequiresShaping(text)) return;
        var window=ImGuiP.FindWindowByName(name);
        if (window.Handle==null || (window.Flags&ImGuiWindowFlags.NoTitleBar)!=0) return;
        var list=window.DrawList; var min=window.Pos;
        var height=ImGuiP.TitleBarHeight(window); var max=min+new Vector2(window.Size.X,height); var font=ImGui.GetFont();
        for(var index=0; index+3<list.VtxBuffer.Size; index++)
        {
            var a=list.VtxBuffer[index]; var b=list.VtxBuffer[index+1]; var c=list.VtxBuffer[index+2]; var d=list.VtxBuffer[index+3];
            if(a.Uv.X>=c.Uv.X || a.Uv.Y>=c.Uv.Y || a.Uv.Y!=b.Uv.Y || b.Uv.X!=c.Uv.X || c.Uv.Y!=d.Uv.Y || d.Uv.X!=a.Uv.X
                || a.Pos.Y!=b.Pos.Y || b.Pos.X!=c.Pos.X || c.Pos.Y!=d.Pos.Y || d.Pos.X!=a.Pos.X
                || a.Pos.X<min.X-1 || c.Pos.X>max.X+1 || a.Pos.Y<min.Y-1 || c.Pos.Y>max.Y+1) continue;
            foreach(var character in original)
            {
                var glyph=ImGui.FindGlyphNoFallback(font,character).Handle; if(glyph==null) glyph=font.Handle->FallbackGlyph;
                if(glyph==null || glyph->U0>=glyph->U1 || glyph->V0>=glyph->V1) continue;
                if(a.Uv.X<glyph->U0-.00001f || a.Uv.Y<glyph->V0-.00001f || c.Uv.X>glyph->U1+.00001f || c.Uv.Y>glyph->V1+.00001f) continue;
                for(var offset=0; offset<4; offset++) list.Handle->VtxBuffer.Data[index+offset].Col&=0x00ffffff;
                index+=3; break;
            }
        }
        var style=ImGui.GetStyle(); var fontSize=font.FontSize*font.Scale*ImGui.GetIO().FontGlobalScale*window.FontWindowScale;
        var layout=renderer.GetLayout(text,fontSize); var measured=layout.Size;
        var collapse=(window.Flags&(ImGuiWindowFlags.NoCollapse|ImGuiWindowFlags.Modal))==0;
        var left=style.FramePadding.X+(collapse&&style.WindowMenuButtonPosition==ImGuiDir.Left?fontSize+style.ItemInnerSpacing.X:0);
        var right=style.FramePadding.X+(window.HasCloseButton?fontSize+style.ItemInnerSpacing.X:0)+(collapse&&style.WindowMenuButtonPosition==ImGuiDir.Right?fontSize+style.ItemInnerSpacing.X:0);
        right += AdditionalTitleButtonWidth(owner, fontSize);
        var p=min+new Vector2(left+Math.Max(0,window.Size.X-left-right-measured.X)*style.WindowTitleAlign.X-Math.Min(0,layout.Rasterize().Offset.X),(height-measured.Y)*.5f);
        list.PushClipRect(Vector2.Max(min+new Vector2(left,0),window.OuterRectClipped.Min),Vector2.Min(max-new Vector2(right,0),window.OuterRectClipped.Max),false);
        try { MaterialText.AddText(list,font,fontSize,p,ImGui.GetColorU32(ImGuiCol.Text),text); }
        finally { list.PopClipRect(); }
    }
}
