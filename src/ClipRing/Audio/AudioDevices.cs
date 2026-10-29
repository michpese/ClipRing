using ClipRing.Core;
using NAudio.CoreAudioApi;

namespace ClipRing.Audio;

public sealed record AudioDeviceOption(string Id, string Name);

public static class AudioDevices
{
    public static List<AudioDeviceOption> List(DataFlow flow)
    {
        var list = new List<AudioDeviceOption>
        {
            new("", Localization.Loc.T(flow == DataFlow.Render ? "Audio_DefaultOutput" : "Audio_DefaultInput")),
        };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                    list.Add(new AudioDeviceOption(device.ID, device.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Audiogeräte konnten nicht aufgelistet werden: " + ex.Message);
        }
        return list;
    }

    /// <summary>Resolves an empty id to the current Windows default device.</summary>
    public static MMDevice? Resolve(MMDeviceEnumerator enumerator, DataFlow flow, string id)
    {
        try
        {
            if (!string.IsNullOrEmpty(id))
            {
                var device = enumerator.GetDevice(id);
                if (device.State == DeviceState.Active) return device;
                device.Dispose();
                Log.Warn($"Audiogerät {id} ist nicht aktiv, verwende Standardgerät");
            }
            return enumerator.GetDefaultAudioEndpoint(flow, flow == DataFlow.Render ? Role.Multimedia : Role.Console);
        }
        catch (Exception ex)
        {
            Log.Warn($"Kein {(flow == DataFlow.Render ? "Ausgabe" : "Aufnahme")}gerät verfügbar: {ex.Message}");
            return null;
        }
    }

    public static string DefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, flow == DataFlow.Render ? Role.Multimedia : Role.Console);
            return device.ID;
        }
        catch
        {
            return "";
        }
    }
}
