using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DBManager.Models;

public class Parameter
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string BrokerName { get; set; } = default!;

    [Required]
    public string Path { get; set; } = default!;

    [Required]
    public string Name { get; set; } = default!;  // Name of the parameter

    public string? LocatedIn { get; set; }  // e.g., query, header, body

    public string? Type { get; set; }       // e.g., string, int, boolean

    public string? Value { get; set; }      // default or example value

    [ForeignKey(nameof(BrokerName))]
    public Broker Broker { get; set; } = default!;
}
