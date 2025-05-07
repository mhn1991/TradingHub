using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace DBManager.Models;

public class Broker
{
    [Key]
    public string Name { get; set; } = default!;  // Primary Key

    public string? APIKey { get; set; }

    public string? SecretKey { get; set; }

    [Required]
    public string BaseURL { get; set; } = default!;

    public ICollection<Endpoint> Endpoints { get; set; } = new List<Endpoint>();
}

