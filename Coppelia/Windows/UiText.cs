using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Resources;
using System.Text.RegularExpressions;
using AethertekUI;
using Dalamud.Bindings.ImGui;

namespace Coppelia.Windows;

internal sealed class UiText : IDisposable
{
    [ThreadStatic] private static UiText? current;
    internal static UiText Current => current ?? throw new InvalidOperationException("Enter the HealBot UI frame before drawing.");
    internal static readonly (string Code,string Name)[] Languages=[("en","English"),("de","Deutsch"),("fr","Français"),
        ("es","Español"),("it","Italiano"),("ru","Русский"),("ja","日本語"),("ko","한국어"),("zh-Hans","简体中文"),("vi","Tiếng Việt"),("pt-BR","Português (Brasil)"),
        ("id","Bahasa Indonesia"),("pl","Polski"),("tr","Türkçe"),("hi","हिन्दी")];
    internal static IEnumerable<string> CjkLanguages(string selected) => new[]{"ja","ko","zh-Hans"}.OrderBy(code=>code==selected?0:1);
    private readonly ResourceManager manager;
    private readonly ResourceManager englishManager;
    private readonly Dictionary<string,string> labels;
    private readonly string[] concatenatedPrefixes;
    internal ResourceSet Resources { get; }
    internal IReadOnlyList<string> RequiredText { get; }
    internal CultureInfo Culture { get; }
    internal string Language { get; }
    private readonly Func<UiFontRole,IDisposable> pushFont;
    private readonly (Regex Pattern, string Key, string Prefix, int ArgumentCount)[] messageTemplates;
    internal UiText(string language, Func<UiFontRole,IDisposable> pushFont)
    {
        Language=Languages.Any(l=>l.Code==language)?language:"en";
        Culture=CultureInfo.GetCultureInfo(Language);
        manager=new ResourceManager("Coppelia.Localization.Strings_"+Language.Replace('-','_'),typeof(UiText).Assembly);
        Resources=manager.GetResourceSet(CultureInfo.InvariantCulture,true,false) ?? throw new MissingManifestResourceException(Language);
        englishManager=new ResourceManager("Coppelia.Localization.Strings_en",typeof(UiText).Assembly);
        var english=englishManager.GetResourceSet(CultureInfo.InvariantCulture,true,false) ?? throw new MissingManifestResourceException("en");
        RequiredText=Values(Resources).Concat(Values(english)).Concat(Languages.Where(l => l.Code != "hi").Select(l=>l.Name)).Distinct().ToArray();
        labels=english.Cast<DictionaryEntry>().ToDictionary(entry=>(string)entry.Value!,entry=>(string)entry.Key,StringComparer.OrdinalIgnoreCase);
        if (labels.Count!=Resources.Cast<DictionaryEntry>().Count() || labels.Values.Any(key=>string.IsNullOrWhiteSpace(Resources.GetString(key,false))))
            throw new MissingManifestResourceException("Incomplete HealBot UI translations for "+Language);
        concatenatedPrefixes=labels.Keys.Where(key=>key.EndsWith(' ') || key.EndsWith('.')).OrderByDescending(key=>key.Length).ToArray();
        this.pushFont=pushFont;
        // Service messages remain English in logs; only their UI copies are localized.
        var parameter = new Regex(@"\{(\d+)(?::([^}]+))?\}");
        messageTemplates = labels.Keys.OrderByDescending(key=>key.Length)
            .Where(key => parameter.IsMatch(key)).Select(key =>
            {
                var argumentCount = 0;
                var pattern = "^";
                var offset = 0;
                foreach (Match hole in parameter.Matches(key))
                {
                    var index = int.Parse(hole.Groups[1].Value, CultureInfo.InvariantCulture);
                    pattern += Regex.Escape(key[offset..hole.Index]) + $"(?<arg{index}>.*?)";
                    argumentCount = Math.Max(argumentCount, index + 1);
                    offset = hole.Index + hole.Length;
                }
                pattern += Regex.Escape(key[offset..]) + "$";
                return (new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(20)), key, key[..parameter.Match(key).Index], argumentCount);
            }).ToArray();
    }
    internal static string T(string english)
        => Translate(english, false);
    internal static string Status(string english)
        => Translate(english, true);
    internal static string Action(string english)
        => english is "Idle" or "Blocked" or "Travel" or "Casting" or "Movement" or "Holding" ? T(english) : english;
    private static string Translate(string english, bool serviceContext)
    {

        if (Current.labels.TryGetValue(english,out var resourceKey)) return Current.Resources.GetString(resourceKey,false)!;
        foreach (var template in Current.messageTemplates)
        {
            if (!serviceContext && template.Prefix.Length==0) continue;
            if (!english.StartsWith(template.Prefix,StringComparison.Ordinal)) continue;
            var match = template.Pattern.Match(english);
            if (!match.Success) continue;
            // Captures are already formatted service values; keep empty arguments and identifiers exact.
            var args = Enumerable.Range(0, template.ArgumentCount).Select(index =>
            {
                var value = match.Groups[$"arg{index}"].Value;
                return TranslateArgument(template.Key,index,value);
            }).ToArray();
            return string.Format(Current.Culture, Current.Resources.GetString(Current.labels[template.Key], false)!, args);
        }
        foreach (var prefix in Current.concatenatedPrefixes)
            if (english.Length>prefix.Length && english.StartsWith(prefix,StringComparison.Ordinal))
                return Current.Resources.GetString(Current.labels[prefix],false)!+" "+T(english[prefix.Length..]);
        if (serviceContext && english.Contains(" | ",StringComparison.Ordinal)) return string.Join(" | ",english.Split(" | ",StringSplitOptions.None).Select(Status));
        return english; // External names, command tokens and raw runtime data retain their original values.
    }
    private static string TranslateArgument(string format,int index,string value)
    {
        if (index == 0 && format == "HealBot requires {0}. Install the missing plugin(s) or disable automation in settings.")
            return string.Join(", ", value.Split(", ",StringSplitOptions.None).Select(T));
        if (index==0 && format is "Attack action: {0}" or "Healing action: {0} | Attack action: {1}") return Action(value);
        if (index==1 && format=="Healing action: {0} | Attack action: {1}") return Action(value);
        return LocalizeArgument(format,index) ? Status(value) : value;
    }
    // Only authored status/label positions are translated. Identity, action and job-name positions stay raw.
    private static bool LocalizeArgument(string format,int index)
    {
        if (format is "Filters: players {0}, chocobos {1}, NPC party {2}, friendly battle NPCs {3}." or
            "Healing: {0} Attacking: {1}" or "{0}; safe ground fallback ({1})" or "HealRider: {0}: {1}" or
            "Heal {0}; attack {1}") return true;
        if (format == "Role: {0}.{1} State: {2} Next: {3} Identity: {4}") return index < 4;
        if (format is "{0} [{1}] - {2}" or "Active: {0}/{1} | Live: {2} | Saved: {3}") return index == (format.StartsWith("Active:") ? 3 : 2);
        if(format=="Watching {0}/{1} active targets. Saved targets: {2}.") return index==2;
        if (format is "Watching {0} targets. {1}" or "Target {0} blocked: {1}" or "{0} - {1}" or
            "{0}: {1} Click to open the main window.")
            return index == 1;
        if (format == "{0}: {1} [{2}]") return index is 0 or 2;
        return index == 0 && (format is "Primary state: {0}" or "Next action: {0}" or "Provider: {0}" or
            "Healing: {0}" or "Attacking: {0}" or "Remote healing: {0}" or "Remote chase: {0}" or
            "JOAT: {0}" or "Travel: {0}" or "HP: {0}" or "Local Newb HP: {0}" or "LOS: {0}" or "Rescue: {0}" or
            "Blocked: {0}" or "Holding: {0}" or "Holding: {0}." or "Resolve: {0}" or "Pairing: {0} - {1}" or
            "mount unavailable: {0}" or "Landing unavailable: {0}" or "Dismount unavailable: {0}" or "flight unavailable: {0}" or
            "Public-instance rendezvous: {0}" or "{0}; waiting for a newer travel snapshot" or
            "Queueing {0} at {1:F1} yalms" or "vnavmesh rejected {0} route startup; waiting for activity to clear or a new travel snapshot" or
            "HealRider Blocked: {0}" or "HealRider cleanup: {0}" or "HealRider: {0}" or
            "Helper cleanup timed out waiting for {0}; waiting for coordinated cleanup retry." or
            "{0} setup saved. The operating role remains Off." or "{0} setup saved and started." or " Behavior: {0}." or
            "{0}: {1}" or "{0} - tracks {1}" or "Enable {0}" or "Role: {0}" or "Operating role: {0}" or
            "Selected job: {0}" or "Powerlevel job: {0}." or "Use /healbot on to resume {0}." or
            "{0} target {1} is selected but remote." or "The exact {0} target is remote." or
            "QST controls the active mode ({0}); the saved local choice resumes after release." or
            "QST controls summoning ({0}); the saved local setting resumes after release." or
            "JOAT attack mode: {0}; both use RSR Manual targeting during genuinely idle healing cycles." or
            "Attacking waits for healing readiness: {0}" or "Holding: the current cast blocks attacking. {0}" or
            "Holding: healing is blocked. {0}" or "Blocked: healing is not ready. {0}" or "RSR Manual ({0}) on {1} at {2:P0} HP." or
            "RSR Manual ({0}) waiting; no engaged hostile is eligible within the paired range." or
            "RSR Manual ({0}) waiting; the selected hostile disappeared.");
    }
    internal static string F(string english,params object?[] args) => string.Format(Current.Culture,T(english),
        args.Select((value,index)=>value is Enum e?T(e.ToString()):value is string s?TranslateArgument(english,index,s):value).ToArray());
    internal static string F(FormattableString text) => F(text.Format,text.GetArguments());
    internal static IDisposable Font(UiFontRole role) => Current.pushFont(role);
    internal static WindowFontScaleScope FontScale(float multiplier) => new(multiplier);
    internal readonly struct WindowFontScaleScope : IDisposable
    {
        private readonly float previous;
        internal WindowFontScaleScope(float multiplier)
        {
            previous = ImGuiP.GetCurrentWindow().FontWindowScale;
            ImGui.SetWindowFontScale(previous * multiplier);
        }
        public void Dispose() => ImGui.SetWindowFontScale(previous);
    }
    internal Scope Enter() => new(this);
    internal readonly struct Scope : IDisposable
    {
        private readonly UiText? previous;
        internal Scope(UiText value) { previous=current; current=value; }
        public void Dispose() => current=previous;
    }
    internal static string Date(DateTimeOffset? date) => date?.ToLocalTime().ToString("g",Current.Culture) ?? T("Never");
    internal ushort[] GlyphRanges()
    {
        var chars=RequiredText.Select(MaterialText.NativeGlyphText).SelectMany(t=>t).Where(c=>!char.IsControl(c))
            .Concat(Enumerable.Range(0x20,0x024F-0x20+1).Select(i=>(char)i))
            .Concat(Enumerable.Range(0x0400,0x052F-0x0400+1).Select(i=>(char)i)).Concat("—").Distinct().Order().ToArray();
        var result=new List<ushort>();
        for(var index=0;index<chars.Length;index++)
        {
            var first=chars[index]; var last=first;
            while(index+1<chars.Length && chars[index+1]==last+1) last=chars[++index];
            result.Add(first); result.Add(last);
        }
        result.Add(0); return result.ToArray();
    }
    internal static IEnumerable<string> Values(ResourceSet set) => set.Cast<DictionaryEntry>().Select(e=>(string)e.Value!);
    public void Dispose() { manager.ReleaseAllResources();englishManager.ReleaseAllResources(); }
}
