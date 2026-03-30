using Microsoft.AspNetCore.Mvc;
using NavalArchive.Api.Contracts;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ListsController : ControllerBase
{
    private readonly DynamicListService _lists;

    public ListsController(DynamicListService lists)
    {
        _lists = lists;
    }

    [HttpGet("{entity}")]
    public async Task<ActionResult<DynamicListResponseDto>> GetList(
        string entity,
        [FromQuery] DynamicListQueryDto query,
        CancellationToken ct = default
    )
    {
        try
        {
            var result = await _lists.GetListAsync(entity, query, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
