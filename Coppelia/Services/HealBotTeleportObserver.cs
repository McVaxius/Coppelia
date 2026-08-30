using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Coppelia.Services;

internal unsafe sealed class HealBotTeleportObserver : IDisposable
{
    private Hook<Telepo.Delegates.Teleport>? teleportHook;

    public event Action<uint, byte>? OnTeleportAccepted;

    public HealBotTeleportObserver(IGameInteropProvider gameInterop)
    {
        try
        {
            var address = Telepo.Addresses.Teleport.Value;
            if (address == 0)
            {
                Plugin.Log.Warning("[Coppelia][Pairing] Teleport observation address is unavailable.");
                return;
            }

            teleportHook = gameInterop.HookFromAddress<Telepo.Delegates.Teleport>(address, TeleportDetour);
            teleportHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Coppelia][Pairing] Could not observe accepted Newb teleports.");
        }
    }

    private bool TeleportDetour(Telepo* self, uint aetheryteId, byte subIndex)
    {
        var accepted = teleportHook!.Original(self, aetheryteId, subIndex);
        if (accepted)
            OnTeleportAccepted?.Invoke(aetheryteId, subIndex);
        return accepted;
    }

    public void Dispose()
    {
        var hook = teleportHook;
        teleportHook = null;
        if (hook == null)
            return;

        hook.Disable();
        hook.Dispose();
    }
}
