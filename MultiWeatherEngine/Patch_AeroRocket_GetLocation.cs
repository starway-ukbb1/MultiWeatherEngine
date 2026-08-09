using HarmonyLib;
using SFS.World;
using SFS.World.Drag;

namespace MultiWeatherEngine;

[HarmonyPatch(typeof(Aero_Rocket), "GetLocation")]
internal static class Patch_AeroRocket_GetLocation
{
	private static void Postfix(ref Location __result)
	{
		__result = TyphoonManager.ToAirspeedFrame(__result);
	}
}
