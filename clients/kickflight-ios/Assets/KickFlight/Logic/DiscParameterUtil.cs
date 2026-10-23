using System;

namespace KickFlight.Logic
{
    /// <summary>
    /// Pure reconstruction of the native DiscParameterUtil.CalcCoefficient interpolation.
    /// Inputs are the min/max coefficient floats and the DiscGrowMasterData rate percentage.
    /// </summary>
    public static class DiscParameterUtil
    {
        public static int CalcCoefficient(float minCoefficient, float maxCoefficient, float rate)
        {
            var interpolated = minCoefficient + (maxCoefficient - minCoefficient) * rate / 100f;
            return (int)MathF.Floor(interpolated);
        }
    }
}
