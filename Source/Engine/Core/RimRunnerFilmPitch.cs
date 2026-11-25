namespace RimRunner;

internal static class RimRunnerFilmPitch
{
    internal const int OutputRate = 48000;

    internal static double Exact(int hz) => hz / (double)OutputRate;

    internal static double? Pitched(int hz, float pitch)
    {
        if (pitch == 1f)
        {
            return Exact(hz);
        }

        if (!(pitch > 0f))
        {
            return null;
        }

        uint thousandths = (uint)((float)hz / OutputRate * pitch * 1000f);
        return thousandths == 0 ? null : thousandths / 1000.0;
    }
}
