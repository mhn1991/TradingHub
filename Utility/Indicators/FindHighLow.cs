using Utility.Indicators;

namespace Utility.Services;

public class FindHighLow: Indicator
{
    public void Find(decimal first , decimal second, decimal third)
    {
        // x---x--x
        if (first == second && second == third)
        {
            Console.WriteLine("Found BASE");
        }
        // x--x          x   
        //      x   x--x 
        if ((first == second && second > third) || (first == second && second < third) ||
            (first > second && second == third) || (first >  second && second < third) ||
            (first < second && second == third) || (first <  second && second > third))
        {
            Console.WriteLine("We need to return the second");
        }
    }
}