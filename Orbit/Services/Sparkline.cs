using System;

namespace Orbit.Services;

public static class Sparkline
{
    // Reduz a série a no máximo maxPoints, preservando primeiro e último.
    public static double[] Subsample(double[] values, int maxPoints)
    {
        if (maxPoints < 2) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        if (values.Length <= maxPoints) return values;
        var result = new double[maxPoints];
        double step = (values.Length - 1) / (double)(maxPoints - 1);
        for (int i = 0; i < maxPoints; i++)
            result[i] = values[(int)Math.Round(i * step)];
        return result;
    }
}
