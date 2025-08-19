using DBManager.Data;
using DBManager.Models;
using Microsoft.EntityFrameworkCore;
using Utility.Converter.Delegates;

namespace DBManager.Services;

public class UnitConverter : IUnitConverter
{
    private readonly AppDbContext _context;
    private readonly Dictionary<FormulaType, ConversionFormula> _formulaMap;

    // In-memory cache for conversions
    private readonly Dictionary<(string, string), UnitConversion> _conversionCache = new();

    public UnitConverter(AppDbContext context)
    {
        _context = context;

        _formulaMap = new Dictionary<FormulaType, ConversionFormula>
        {
            { FormulaType.Factor, ConversionFormulas.FactorOnly },
            { FormulaType.FactorOffset, ConversionFormulas.FactorOffset }
        };
    }

    public decimal Convert(decimal value, string fromUnit, string toUnit)
    {
        if (string.IsNullOrWhiteSpace(fromUnit) || string.IsNullOrWhiteSpace(toUnit))
            throw new ArgumentNullException("Units cannot be null or empty.");

        if (fromUnit.Equals(toUnit, StringComparison.OrdinalIgnoreCase))
            return value;

        // Check cache first
        if (!_conversionCache.TryGetValue((fromUnit, toUnit), out var conversion))
        {
            conversion = _context.UnitConversions
                .Include(uc => uc.FromUnit)
                .Include(uc => uc.ToUnit)
                .FirstOrDefault(uc => uc.FromUnit.Symbol == fromUnit && uc.ToUnit.Symbol == toUnit);

            if (conversion == null)
                throw new ArgumentException($"No conversion path from {fromUnit} to {toUnit}.");

            _conversionCache[(fromUnit, toUnit)] = conversion; // Cache it
        }

        var formula = _formulaMap[conversion.FormulaType];
        return formula(value, conversion.FormulaValue, conversion.FormulaOffset ?? 0);
    }

    public IEnumerable<string> GetSupportedUnits(string unitType)
    {
        return _context.Units
            .Where(u => u.UnitType == unitType)
            .Select(u => u.Symbol)
            .ToList();
    }
}
