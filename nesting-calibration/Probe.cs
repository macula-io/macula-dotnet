namespace Calibration;
public static class Probe
{
    public static int FourDeep(int a, int b, int c, int d)
    {
        if (a == 0)
        {
            if (b == 0)
            {
                if (c == 0)
                {
                    if (d == 0)
                    {
                        return 4;
                    }
                }
            }
        }
        return 0;
    }
}
