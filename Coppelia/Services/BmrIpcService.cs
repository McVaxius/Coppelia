using System.Text.Json;

namespace Coppelia.Services;

internal sealed class BmrIpcService
{
    private readonly Func<object?> providerIdentity;
    private readonly Func<string, string?> getPreset;
    private readonly Func<string?> getActive;
    private readonly Func<bool> getForceDisabled;
    private readonly Func<string, bool> setActive;
    private readonly Func<bool> clearActive;
    private readonly Func<bool> setForceDisabled;
    private readonly Func<string, bool, bool>? createPreset;
    private readonly Func<string, bool>? deletePreset;
    private readonly Func<string, (object Identity, IReadOnlyDictionary<string, IReadOnlySet<string>> Options)?> definitionReader;
    private object? capturedProvider;
    private PresetState? original;
    private PresetState? owned;
    private PresetState? submitted;
    private string? selectedPreset;
    private bool ownershipLost;
    private BmrJoatPreset? damagePreset;

    public BmrIpcService()
        : this(GetProviderIdentity,
            name => Plugin.PluginInterface.GetIpcSubscriber<string, string>("BossMod.Presets.Get").InvokeFunc(name),
            () => Plugin.PluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive").InvokeFunc(),
            () => Plugin.PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.GetForceDisabled").InvokeFunc(),
            name => Plugin.PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive").InvokeFunc(name),
            () => Plugin.PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive").InvokeFunc(),
            () => Plugin.PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.SetForceDisabled").InvokeFunc(),
            (json, overwrite) => Plugin.PluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create").InvokeFunc(json, overwrite),
            name => Plugin.PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.Delete").InvokeFunc(name))
    {
    }

    internal BmrIpcService(Func<object?> providerIdentity, Func<string, string?> getPreset,
        Func<string?> getActive, Func<bool> getForceDisabled, Func<string, bool> setActive,
        Func<bool> clearActive, Func<bool> setForceDisabled,
        Func<string, bool, bool>? createPreset = null, Func<string, bool>? deletePreset = null,
        Func<string, (object Identity, IReadOnlyDictionary<string, IReadOnlySet<string>> Options)?>? definitionReader = null)
    {
        this.providerIdentity = providerIdentity;
        this.getPreset = getPreset;
        this.getActive = getActive;
        this.getForceDisabled = getForceDisabled;
        this.setActive = setActive;
        this.clearActive = clearActive;
        this.setForceDisabled = setForceDisabled;
        this.createPreset = createPreset;
        this.deletePreset = deletePreset;
        this.definitionReader = definitionReader ?? BmrJoatPresetContract.ReadDefinition;
    }

    public bool HasSessionSnapshot => original != null;
    public string LastFailure { get; private set; } = string.Empty;

    internal bool HasDamageCapability(uint jobId, bool fullRotation, out string blocker)
        => BmrJoatPresetContract.TryCreate(jobId, fullRotation, "Coppelia JOAT readiness",
            out _, out blocker, definitionReader);

    internal bool PrepareDamage(uint jobId, bool fullRotation)
    {
        if (!BmrJoatPresetContract.TryCreate(jobId, fullRotation, damagePreset?.Name ?? $"Coppelia JOAT {Guid.NewGuid():N}",
                out var requested, out var blocker, definitionReader))
            return Fail(blocker);
        if (!Prepare()) return false;
        try
        {
            if (damagePreset != null && (!ReferenceEquals(damagePreset.ModuleIdentity, requested!.ModuleIdentity)
                    || !BmrJoatPresetContract.SamePreset(damagePreset.Json, requested.Json)))
            {
                if (!RemoveOwnedPreset()) return false;
                // A mode/job change gets a distinct name; no user preset is overwritten.
                if (!BmrJoatPresetContract.TryCreate(jobId, fullRotation, $"Coppelia JOAT {Guid.NewGuid():N}",
                        out requested, out blocker, definitionReader)) return Fail(blocker);
            }
            if (damagePreset == null)
            {
                if (createPreset == null || getPreset(requested!.Name) != null || !createPreset(requested.Json, false))
                    return Fail("BMR rejected creation of the owned JOAT preset; no existing preset was overwritten.");
                damagePreset = requested;
            }
            if (!BmrJoatPresetContract.SamePreset(getPreset(damagePreset.Name), damagePreset.Json))
                return Fail("BMR did not retain the exact Full Dhog and manual-target strategies; JOAT is blocked.");
            selectedPreset = damagePreset.Name;
            return Hold();
        }
        catch (Exception ex) { return Fail($"BMR Dhog preset preparation failed: {ex.GetBaseException().Message}"); }
    }

    internal void ResetOwnership()
    {
        if (original == null)
            ownershipLost = false;
    }

    public bool Prepare(string? presetName = null)
    {
        try
        {
            if (ownershipLost)
                return Fail("BMR ownership changed; explicitly reactivate before acquiring it again.");
            if (presetName != null && !IsExactPreset(presetName))
                return Fail("The BMR JOAT preset is unavailable or has a different exact name.");
            if (original == null)
            {
                capturedProvider = providerIdentity();
                if (capturedProvider == null)
                    return Fail("An unambiguous loaded BMR provider is required.");
                var state = ReadState();
                if (state.Name != null && !IsExactPreset(state.Name))
                    return Fail("The previous BMR preset cannot be restored by exact name.");
                original = owned = state;
            }
            selectedPreset = presetName;
            return Hold();
        }
        catch (Exception ex) { return Fail($"BMR preset preparation failed: {ex.GetBaseException().Message}"); }
    }

    public bool Hold()
        => !ownershipLost && (original == null || ChangeOwnedState(new PresetState(true, null)));

    public bool Resume()
    {
        if (original == null || selectedPreset == null)
            return Fail("BMR JOAT control has not been acquired.");
        try
        {
            if (!IsExactPreset(selectedPreset))
                return Fail("The selected BMR JOAT preset disappeared.");
            if (damagePreset != null && (!ReferenceEquals(definitionReader(damagePreset.ModuleName)?.Identity, damagePreset.ModuleIdentity)
                    || !BmrJoatPresetContract.SamePreset(getPreset(damagePreset.Name), damagePreset.Json)))
                return Fail("The owned Dhog module or preset changed; BMR JOAT damage is blocked.");
            return ChangeOwnedState(new PresetState(false, selectedPreset));
        }
        catch (Exception ex) { return Fail($"BMR JOAT resume failed: {ex.GetBaseException().Message}"); }
    }

    public bool RestoreSessionSnapshot()
    {
        if (original == null)
            return true;
        try
        {
            if (!IsSameProvider())
            {
                ownershipLost = true;
                ForgetSession();
                return Fail("BMR reloaded; its replacement was left unchanged.");
            }
            var current = ReadState();
            if (current == original)
            {
                if (!RemoveOwnedPreset()) return false;
                ForgetSession();
                LastFailure = string.Empty;
                return true;
            }
            if (current != owned && current != submitted)
            {
                ownershipLost = true;
                ForgetSession();
                return Fail("BMR was changed by another owner; that selection was retained.");
            }
            if (original.Name != null && !IsExactPreset(original.Name))
                return Fail("The captured BMR preset no longer exists; restoration is pending.");
            var expected = original;
            if (current != expected)
                WriteState(expected);
            if (ReadState() != expected)
                return Fail("BMR restoration could not be confirmed.");
            owned = expected;
            if (!RemoveOwnedPreset()) return false;
            ForgetSession();
            LastFailure = string.Empty;
            return true;
        }
        catch (Exception ex) { return Fail($"BMR restoration failed: {ex.GetBaseException().Message}"); }
    }

    private bool ChangeOwnedState(PresetState requested)
    {
        try
        {
            if (!IsSameProvider())
            {
                ownershipLost = true;
                ForgetSession();
                return Fail("BMR reloaded; reacquire control before attacking.");
            }
            var current = ReadState();
            if (damagePreset != null && current.Name == damagePreset.Name
                && !BmrJoatPresetContract.SamePreset(getPreset(damagePreset.Name), damagePreset.Json))
            {
                ownershipLost = true;
                ForgetSession();
                return Fail("The active BMR JOAT preset was edited externally; it was left unchanged.");
            }
            if (current != owned && current != submitted)
            {
                ownershipLost = true;
                ForgetSession();
                return Fail("BMR control was changed by another owner.");
            }
            if (current != requested)
            {
                // Either the last confirmed state or this submitted state can be
                // ours when a rejected/uncertain native call needs reconciliation.
                submitted = requested;
                WriteState(requested);
            }
            if (ReadState() != requested)
                return Fail("The requested BMR state could not be confirmed.");
            owned = requested;
            submitted = null;
            LastFailure = string.Empty;
            return true;
        }
        catch (Exception ex) { return Fail($"BMR state control failed: {ex.GetBaseException().Message}"); }
    }

    private PresetState ReadState()
    {
        var forceDisabled = getForceDisabled();
        var name = getActive();
        if (forceDisabled != getForceDisabled())
            throw new InvalidOperationException("BMR state changed while it was read.");
        if (!forceDisabled && name != null && string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("BMR returned an ambiguous active preset.");
        return new PresetState(forceDisabled, forceDisabled ? null : name);
    }

    private void WriteState(PresetState state)
    {
        // These setters return false for some already-applied states. Readback is authoritative.
        if (state.ForceDisabled) _ = setForceDisabled();
        else if (state.Name != null) _ = setActive(state.Name);
        else _ = clearActive();
    }

    private bool IsExactPreset(string name)
    {
        var json = getPreset(name);
        if (json == null)
            return false;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("Name", out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString() == name;
    }

    private bool IsSameProvider()
        => capturedProvider != null && ReferenceEquals(capturedProvider, providerIdentity());

    private bool RemoveOwnedPreset()
    {
        if (damagePreset == null) return true;
        var current = getPreset(damagePreset.Name);
        if (current == null) { damagePreset = null; return true; }
        if (!BmrJoatPresetContract.SamePreset(current, damagePreset.Json))
        {
            damagePreset = null;
            return true; // Retain a preset edited by another owner.
        }
        if (ReadState().Name == damagePreset.Name)
            return Fail("The owned JOAT preset is still active; cleanup is pending.");
        if (deletePreset == null)
            return Fail("BMR preset cleanup is unavailable.");
        _ = deletePreset(damagePreset.Name);
        if (getPreset(damagePreset.Name) != null)
            return Fail("BMR did not confirm owned JOAT preset cleanup.");
        damagePreset = null;
        return true;
    }

    private void ForgetSession()
    {
        capturedProvider = null;
        original = owned = submitted = null;
        selectedPreset = null;
        damagePreset = null;
    }

    private bool Fail(string reason)
    {
        LastFailure = reason;
        return false;
    }

    private static object? GetProviderIdentity()
    {
        var installed = Plugin.PluginInterface.InstalledPlugins;
        if (!installed.Any(plugin => plugin.IsLoaded && plugin.InternalName == "BossModReborn")
            || installed.Any(plugin => plugin.IsLoaded && plugin.InternalName is "BossMod" or "vbm"))
            return null;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name == "BossModReborn").ToArray();
        return assemblies.Length == 1 ? assemblies[0] : null;
    }

    private sealed record PresetState(bool ForceDisabled, string? Name);
}
