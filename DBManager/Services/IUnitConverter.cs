namespace DBManager.Services;

public interface IUnitConverter
{
    decimal Convert(decimal value, string fromUnit, string toUnit);
    IEnumerable<string> GetSupportedUnits(string unitType);
}