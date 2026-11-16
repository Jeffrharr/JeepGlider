using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(JeepGlider.Mod), "JeepGlider", "1.0.1", "Joof")]
[assembly: MelonGame("Funselektor", "over the hill")]

namespace JeepGlider;

// A hang glider roof cosmetic: honk to open it and fly, honk again to fold it.
// See Glider for the game side and GliderMath for the flight model.
public class Mod : MelonMod
{
    internal static MelonLogger.Instance Log;

    internal static MelonPreferences_Entry<bool> Enabled;
    internal static MelonPreferences_Entry<bool> Flight;
    internal static MelonPreferences_Entry<float> WingLoading;
    internal static MelonPreferences_Entry<float> Stability;
    internal static MelonPreferences_Entry<float> PitchRange;
    internal static MelonPreferences_Entry<bool> InvertPitch;
    internal static MelonPreferences_Entry<float> OpenSeconds;
    internal static MelonPreferences_Entry<float> Span;

    GameObject host;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        var c = MelonPreferences.CreateCategory("JeepGlider");
        Enabled = c.CreateEntry("Enabled", true, description: "The JeepGlider roof cosmetic, free in the garage. Honk to open it, honk again to fold it. Other players see it if they have the mod too.");
        Flight = c.CreateEntry("Flight", true, description: "The open glider really flies the car. Off = looks only. In multiplayer the host needs JeepGlider too.");
        WingLoading = c.CreateEntry("WingLoading", 7f, description: "Kilograms of car per square metre of sail. A real hang glider is about 7. Lower = floatier, slower, glides further.");
        Stability = c.CreateEntry("Stability", 1f, description: "How firmly it holds its glide and banks with the steering. 0 = no help at all.");
        PitchRange = c.CreateEntry("PitchRange", 1f, description: "How far the left stick's up and down pitches the glider. 0 = off. Above 1 lets pulling back stall it.");
        InvertPitch = c.CreateEntry("InvertPitch", false, description: "Off: stick forward dives, stick back pulls up, like a hang glider's bar. On: the other way round.");
        OpenSeconds = c.CreateEntry("OpenSeconds", 1.5f, description: "How long it takes to open or fold.");
        Span = c.CreateEntry("Span", 10f, description: "Wingspan of the model in metres. Only changes how it looks.");
        Net.Register();
    }

    // The flight model with the player's settings
    internal static Airframe Airframe()
    {
        var a = JeepGlider.Airframe.Default;
        return new Airframe(Mathf.Max(WingLoading.Value, 0.5f), a.AspectRatio, a.Cl0, a.StallAngle, a.Cd0, a.Oswald, a.SideForce,
            a.TrimAngle, a.TrimDown * PitchRange.Value, a.TrimUp * PitchRange.Value, a.MaxBank, a.StabilityHz, a.DampingRatio);
    }

    public override void OnUpdate()
    {
        JoofOTHFramework.HillNet.Tick();
        // Unity destroys the helper object while the game closes; don't make a new one then
        if (host != null || JoofOTHFramework.Core.Quitting) return;
        host = new GameObject("JeepGlider") { hideFlags = HideFlags.HideAndDontSave };
        Object.DontDestroyOnLoad(host);
        host.AddComponent<Glider>();
    }
}
