using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TimelineController : ControllerBase
{
    private static readonly object[] Events =
    {
        new { Year = 1939, Month = "Sep", Title = "War Begins", Description = "Germany invades Poland. Royal Navy begins blockade." },
        new { Year = 1940, Month = "May", Title = "Dunkirk Evacuation", Description = "Operation Dynamo: 338,000 Allied troops evacuated by naval vessels." },
        new { Year = 1941, Month = "May", Title = "Bismarck Sunk", Description = "HMS Hood lost; Bismarck hunted down and sunk by Royal Navy." },
        new { Year = 1941, Month = "Dec", Title = "Pearl Harbor", Description = "Japanese carrier strike on US Pacific Fleet. America enters the war." },
        new { Year = 1942, Month = "May", Title = "Coral Sea", Description = "First carrier vs carrier battle. Strategic US victory." },
        new { Year = 1942, Month = "Jun", Title = "Battle of Midway", Description = "Turning point in Pacific. Four Japanese carriers sunk." },
        new { Year = 1942, Month = "Nov", Title = "Guadalcanal", Description = "Naval battles around Solomon Islands. USS Enterprise heavily damaged." },
        new { Year = 1943, Month = "Jul", Title = "Allied Invasion of Sicily", Description = "Operation Husky. Largest amphibious assault to date." },
        new { Year = 1944, Month = "Jun", Title = "D-Day", Description = "Normandy landings. 5,000 vessels in Operation Neptune." },
        new { Year = 1944, Month = "Oct", Title = "Leyte Gulf", Description = "Largest naval battle in history. Japanese fleet decimated." },
        new { Year = 1945, Month = "Apr", Title = "Yamato Sunk", Description = "Last Japanese battleship destroyed by US carrier aircraft." },
        new { Year = 1945, Month = "Sep", Title = "V-J Day", Description = "Japanese surrender aboard USS Missouri in Tokyo Bay." }
    };

    [HttpGet]
    public IActionResult GetTimeline()
    {
        return Ok(Events);
    }
}
