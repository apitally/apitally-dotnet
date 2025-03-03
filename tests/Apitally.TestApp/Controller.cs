namespace Apitally.TestApp;

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
}
