namespace DBManager.Models;

using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

[Index(nameof(Symbol), IsUnique = true)]
public class Unit
{
    [Key] public int UnitId { get; set; }

    [Required] [MaxLength(50)] public string Symbol { get; set; } // e.g., "Hour", "Minute", "Celsius"

    [Required] [MaxLength(50)] public string UnitType { get; set; } // e.g., "Time", "Temperature"

    [MaxLength(200)] public string Description { get; set; }
}