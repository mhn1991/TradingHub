namespace Utility.Indicators;

public class AverageTrueRange: Indicator
{
    private int _window;

    AverageTrueRange(int window = 14)
    {
        _window = window;
    }

    public void Calculate(bool isLive=false)
    {
        if (isLive)
        {
            
        }
    }
}