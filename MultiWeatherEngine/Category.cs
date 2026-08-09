using UnityEngine;

namespace MultiWeatherEngine;

public static class Category
{
	public static readonly string[] Names = new string[7] { "TD 热带低压", "TS 热带风暴", "CAT-1", "CAT-2", "CAT-3", "CAT-4", "CAT-5 超强台风" };

	public static readonly double[] PeakWind = new double[7] { 14.0, 24.0, 35.0, 45.0, 54.0, 62.0, 78.0 };

	public static readonly Color[] Tint = (Color[])(object)new Color[7]
	{
		new Color(0.55f, 0.8f, 1f),
		new Color(0.45f, 0.95f, 0.75f),
		new Color(1f, 0.95f, 0.45f),
		new Color(1f, 0.78f, 0.32f),
		new Color(1f, 0.55f, 0.28f),
		new Color(1f, 0.34f, 0.34f),
		new Color(0.98f, 0.3f, 0.75f)
	};

	public static int Clamp(int i)
	{
		if (i < 0)
		{
			return 0;
		}
		if (i <= 6)
		{
			return i;
		}
		return 6;
	}
}
