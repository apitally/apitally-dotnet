using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace Apitally.TestApp;

[ApiController]
[Route("controller/items")]
public sealed class ItemsController : ControllerBase
{
    [HttpGet("{id:int}")]
    [EndpointSummary("Get an item")]
    public IActionResult Get(int id) => Ok(new { id });

    [HttpPost]
    public IActionResult Create(ItemInput input, CancellationToken cancellationToken) =>
        Created($"/controller/items/{input.Id}", input);

    [HttpGet]
    public IActionResult List([FromQuery(Name = "limit")] int limit) => Ok(new { limit });
}

public sealed class ItemInput
{
    [Range(1, 1000)]
    public int Id { get; set; }

    [Required]
    [StringLength(10)]
    public string? Name { get; set; }
}
