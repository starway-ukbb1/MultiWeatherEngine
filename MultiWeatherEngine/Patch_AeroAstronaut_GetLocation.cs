using HarmonyLib;
using SFS.World;
using SFS.World.Drag;

namespace MultiWeatherEngine;

[HarmonyPatch(typeof(Aero_Astronaut), "GetLocation")]
internal static class Patch_AeroAstronaut_GetLocation
{
	private static void Postfix(ref Location __result)
	{
		if (TyphoonConfig.I.affectAstronauts)
		{
			__result = TyphoonManager.ToAirspeedFrame(__result);
		}
	}
}
