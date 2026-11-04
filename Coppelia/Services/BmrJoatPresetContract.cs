using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace Coppelia.Services;

internal sealed record BmrJoatPreset(string Name, string ModuleName, object ModuleIdentity, string Json);

internal static class BmrJoatPresetContract
{
    internal static string? ModuleName(uint jobId) => jobId switch
    {
        24 => "BMRVariantModulePack.Autorotation.Dhog.DhogWHM",
        28 => "BMRVariantModulePack.Autorotation.Dhog.DhogSCH",
        33 => "BMRVariantModulePack.Autorotation.Dhog.DhogAST",
        40 => "BMRVariantModulePack.Autorotation.Dhog.DhogSGE",
        _ => null,
    };

    internal static bool TryCreate(uint jobId, bool fullRotation, string presetName,
        [NotNullWhen(true)] out BmrJoatPreset? preset, out string blocker,
        Func<string, (object Identity, IReadOnlyDictionary<string, IReadOnlySet<string>> Options)?>? definitionReader = null)
    {
        preset = null;
        if (ModuleName(jobId) is not { } moduleName)
        {
            blocker = "BMR JOAT requires a supported healer job.";
            return false;
        }

        try
        {
            var definition = (definitionReader ?? ReadDefinition)(moduleName);
            if (definition == null)
            {
                blocker = "God Mod Companion rotations could not be found for the equipped healer. Load the matching Dhog healer rotations; no other rotation is substituted.";
                return false;
            }
            if (!fullRotation)
            {
                blocker = "The God Mod Companion Dhog rotation is present, but it has no verified DoTs-only damage strategy. Select optional RSR for DoTs-only; full BMR damage must not be substituted.";
                return false;
            }
            // Full BMR intentionally includes Dhog/native healing and raising only
            // while Coppelia's own healing decision is idle. It is not damage-only.
            var choices = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Targeting"] = "Manual",
                ["AOE"] = "ForceST",
                ["DhogHeal"] = "Enabled",
                ["DhogRecipients"] = "Nearby",
                ["DhogRaise"] = "SwiftThenSafe",
                ["DhogRaisePriority"] = "PartyFirst",
            };
            foreach (var choice in choices)
            {
                if (!definition.Value.Options.TryGetValue(choice.Key, out var options) || !options.Contains(choice.Value))
                {
                    blocker = $"The God Mod Companion rotation lacks {choice.Key}={choice.Value}; Full BMR cannot be applied exactly.";
                    return false;
                }
            }
            preset = new(presetName, moduleName, definition.Value.Identity, JsonSerializer.Serialize(new
            {
                Name = presetName,
                Modules = new Dictionary<string, object>
                {
                    [moduleName] = choices.Select(choice => new { Track = choice.Key, Option = choice.Value }).ToArray(),
                },
            }));
            blocker = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            blocker = $"God Mod Companion rotations could not be found or inspected: {ex.GetBaseException().Message}";
            return false;
        }
    }

    internal static bool SamePreset(string? actual, string expected)
    {
        if (actual == null) return false;
        try
        {
            using var first = JsonDocument.Parse(actual);
            using var second = JsonDocument.Parse(expected);
            return JsonElement.DeepEquals(first.RootElement, second.RootElement);
        }
        catch (JsonException) { return false; }
    }

    internal static (object Identity, IReadOnlyDictionary<string, IReadOnlySet<string>> Options)? ReadDefinition(string moduleName)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name == "BossModReborn").ToArray();
        if (assemblies.Length != 1) return null;
        var registry = assemblies[0].GetType("BossMod.Autorotation.RotationModuleRegistry");
        if (registry?.GetMethod("FindType", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, [moduleName]) is not Type type
            || type.FullName != moduleName
            || registry.GetField("Modules", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) is not IDictionary modules
            || !modules.Contains(type) || modules[type] is not { } entry
            || Member(entry, "Definition") is not { } definition
            || Member(definition, "Configs") is not IEnumerable tracks)
            return null;

        var options = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (track == null || Member(track, "InternalName") is not string name
                || Member(track, "Options") is not IEnumerable values)
                continue;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
                if (value != null && Member(value, "InternalName") is string option) names.Add(option);
            options.Add(name, names);
        }
        return (type, options);
    }

    private static object? Member(object value, string name)
        => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value)
            ?? value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
}
