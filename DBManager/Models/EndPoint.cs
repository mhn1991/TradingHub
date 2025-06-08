using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection.Metadata;

namespace DBManager.Models;

public class Endpoint
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string BrokerName { get; set; } = default!;

    [ForeignKey(nameof(BrokerName))]
    public Broker Broker { get; set; } = default!;

    [Required]
    public string Path { get; set; } = default!;

    [Required]
    public string ProtocolType { get; set; } = default!;

    public string? ActionType { get; set; }

    [Required]
    public EndpointType EndpointType { get; set; }

    public ICollection<Parameter> Parameters { get; set; } = new List<Parameter>();
}

