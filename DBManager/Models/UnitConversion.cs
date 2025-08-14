using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DBManager.Models;

namespace DBManager.Models;

public class UnitConversion
{
    [Key]
    public int ConversionId { get; set; }

    [Required]
    public int FromUnitId { get; set; }
    public Unit FromUnit { get; set; }

    [Required]
    public int ToUnitId { get; set; }
    public Unit ToUnit { get; set; }

    [Required]
    public FormulaType FormulaType { get; set; }

    [Required]
    public decimal FormulaValue { get; set; }

    public decimal? FormulaOffset { get; set; } // Optional for FactorOffset conversions
}