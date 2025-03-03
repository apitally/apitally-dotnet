namespace Apitally.TestApp;

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public class ItemsController : ControllerBase
{
    [HttpGet("/controller/items")]
    public IActionResult GetItems()
    {
        var items = new[] { new Item(1, "bob"), new Item(2, "alice") };
        return Ok(items);
    }

    [HttpPost("/controller/items")]
    public IActionResult CreateItem([FromBody] Item item)
    {
        return Created($"/controller/items/{item.Id}", item);
    }
}

public record Item(
    [Required] [Range(1, 1000)] int Id,
    [Required] [StringLength(100, MinimumLength = 2)] string Name
);
