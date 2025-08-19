namespace Utility.Converter.Delegates;

public class ConversionFormulas
{
    public static readonly ConversionFormula FactorOnly = (value, factor, _) => value * factor;
    public static readonly ConversionFormula FactorOffset = (value, factor, offset) => (value * factor) + offset;
}