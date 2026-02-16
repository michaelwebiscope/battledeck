using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    // CRITICAL: Static cache - never cleared, grows indefinitely until OOM
    private static readonly Dictionary<int, byte[]> _imageCache = new();

    /// <summary>
    /// MEMORY LEAK: Each unique ID adds 5MB to static cache. Never evicted.
    /// </summary>
    [HttpGet("{id:int}")]
    public IActionResult GetImage(int id)
    {
        if (!_imageCache.ContainsKey(id))
        {
            // Simulate high-res photo: 5MB dummy byte array
            var dummyImage = new byte[5 * 1024 * 1024];
            new Random().NextBytes(dummyImage);
            _imageCache[id] = dummyImage;
        }

        return File(_imageCache[id], "image/jpeg");
    }
}
