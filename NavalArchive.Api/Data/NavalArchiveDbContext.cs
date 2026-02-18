using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Models;

namespace NavalArchive.Api.Data;

public class NavalArchiveDbContext : DbContext
{
    public NavalArchiveDbContext(DbContextOptions<NavalArchiveDbContext> options)
        : base(options) { }

    public DbSet<Ship> Ships => Set<Ship>();
    public DbSet<ShipClass> ShipClasses => Set<ShipClass>();
    public DbSet<Captain> Captains => Set<Captain>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Seed ShipClasses
        modelBuilder.Entity<ShipClass>().HasData(
            new ShipClass { Id = 1, Name = "Bismarck-class", Type = "Battleship", Country = "Germany" },
            new ShipClass { Id = 2, Name = "Yamato-class", Type = "Battleship", Country = "Japan" },
            new ShipClass { Id = 3, Name = "Iowa-class", Type = "Battleship", Country = "USA" },
            new ShipClass { Id = 4, Name = "Yorktown-class", Type = "Aircraft Carrier", Country = "USA" },
            new ShipClass { Id = 5, Name = "Illustrious-class", Type = "Aircraft Carrier", Country = "UK" },
            new ShipClass { Id = 6, Name = "Shokaku-class", Type = "Aircraft Carrier", Country = "Japan" },
            new ShipClass { Id = 7, Name = "County-class", Type = "Heavy Cruiser", Country = "UK" },
            new ShipClass { Id = 8, Name = "Baltimore-class", Type = "Heavy Cruiser", Country = "USA" },
            new ShipClass { Id = 9, Name = "Fletcher-class", Type = "Destroyer", Country = "USA" },
            new ShipClass { Id = 10, Name = "Tribal-class", Type = "Destroyer", Country = "UK" }
        );

        // Seed Captains (ImageUrl populated by Wikipedia sync)
        modelBuilder.Entity<Captain>().HasData(
            new Captain { Id = 1, Name = "Ernst Lindemann", Rank = "Captain", ServiceYears = 28, ImageUrl = (string?)null },
            new Captain { Id = 2, Name = "Karl Topp", Rank = "Captain", ServiceYears = 30, ImageUrl = (string?)null },
            new Captain { Id = 3, Name = "Kosaku Aruga", Rank = "Admiral", ServiceYears = 35, ImageUrl = (string?)null },
            new Captain { Id = 4, Name = "Toshihira Inoguchi", Rank = "Captain", ServiceYears = 32, ImageUrl = (string?)null },
            new Captain { Id = 5, Name = "John McCrea", Rank = "Captain", ServiceYears = 28, ImageUrl = (string?)null },
            new Captain { Id = 6, Name = "Charles F. Adams", Rank = "Admiral", ServiceYears = 40, ImageUrl = (string?)null },
            new Captain { Id = 7, Name = "William Callaghan", Rank = "Captain", ServiceYears = 35, ImageUrl = (string?)null },
            new Captain { Id = 8, Name = "Glenn Davis", Rank = "Captain", ServiceYears = 30, ImageUrl = (string?)null },
            new Captain { Id = 9, Name = "George Murray", Rank = "Admiral", ServiceYears = 38, ImageUrl = (string?)null },
            new Captain { Id = 10, Name = "Elliott Buckmaster", Rank = "Captain", ServiceYears = 32, ImageUrl = (string?)null },
            new Captain { Id = 11, Name = "Marc Mitscher", Rank = "Admiral", ServiceYears = 42, ImageUrl = (string?)null },
            new Captain { Id = 12, Name = "Frederick Sherman", Rank = "Captain", ServiceYears = 35, ImageUrl = (string?)null },
            new Captain { Id = 13, Name = "Dewey B. Bronson", Rank = "Captain", ServiceYears = 30, ImageUrl = (string?)null },
            new Captain { Id = 14, Name = "Ralph Kerr", Rank = "Captain", ServiceYears = 28, ImageUrl = (string?)null },
            new Captain { Id = 15, Name = "Denis Boyd", Rank = "Admiral", ServiceYears = 36, ImageUrl = (string?)null },
            new Captain { Id = 16, Name = "Arthur Power", Rank = "Captain", ServiceYears = 33, ImageUrl = (string?)null },
            new Captain { Id = 17, Name = "Henry Bovell", Rank = "Captain", ServiceYears = 30, ImageUrl = (string?)null },
            new Captain { Id = 18, Name = "Philip Vian", Rank = "Admiral", ServiceYears = 40, ImageUrl = (string?)null },
            new Captain { Id = 19, Name = "Takatsugu Jojima", Rank = "Captain", ServiceYears = 32, ImageUrl = (string?)null },
            new Captain { Id = 20, Name = "Tamon Yamaguchi", Rank = "Admiral", ServiceYears = 35, ImageUrl = (string?)null },
            new Captain { Id = 21, Name = "Frederick Bell", Rank = "Captain", ServiceYears = 30, ImageUrl = (string?)null },
            new Captain { Id = 22, Name = "Charles McVay", Rank = "Captain", ServiceYears = 28, ImageUrl = (string?)null },
            new Captain { Id = 23, Name = "Walter Deakins", Rank = "Captain", ServiceYears = 32, ImageUrl = (string?)null },
            new Captain { Id = 24, Name = "William Cole", Rank = "Commander", ServiceYears = 25, ImageUrl = (string?)null },
            new Captain { Id = 25, Name = "Ernest Evans", Rank = "Commander", ServiceYears = 22, ImageUrl = (string?)null }
        );

        // Seed 50 famous ships - ImageUrl: real Wikimedia Commons URLs for display, /images/{id} for memory-leak API
        var ships = new List<Ship>
        {
            new Ship { Id = 1, Name = "Bismarck", ClassId = 1, CaptainId = 1, YearCommissioned = 1940, Description = "German battleship, flagship of the Kriegsmarine.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/f/fe/Bundesarchiv_Bild_193-04-1-26%2C_Schlachtschiff_Bismarck.jpg/800px-Bundesarchiv_Bild_193-04-1-26%2C_Schlachtschiff_Bismarck.jpg" },
            new Ship { Id = 2, Name = "Tirpitz", ClassId = 1, CaptainId = 2, YearCommissioned = 1941, Description = "Sister ship to Bismarck, terror of the North Atlantic.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/7/79/Tirpitz-2.jpg/800px-Tirpitz-2.jpg" },
            new Ship { Id = 3, Name = "Yamato", ClassId = 2, CaptainId = 3, YearCommissioned = 1941, Description = "Largest battleship ever built, Imperial Japanese Navy.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/b/b2/Japanese_battleship_Yamato_running_trials_off_Bungo_Strait%2C_20_October_1941.jpg/800px-Japanese_battleship_Yamato_running_trials_off_Bungo_Strait%2C_20_October_1941.jpg" },
            new Ship { Id = 4, Name = "Musashi", ClassId = 2, CaptainId = 4, YearCommissioned = 1942, Description = "Sister ship to Yamato, sunk at Leyte Gulf.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/b/be/Japanese_battleship_Musashi_underway_in_1944_%28NH_63473%29_%28cropped%29.jpg/800px-Japanese_battleship_Musashi_underway_in_1944_%28NH_63473%29_%28cropped%29.jpg" },
            new Ship { Id = 5, Name = "USS Iowa", ClassId = 3, CaptainId = 5, YearCommissioned = 1943, Description = "Lead ship of Iowa class, served in Pacific.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/e/ea/BB61_USS_Iowa_BB61_broadside_USN.jpg/800px-BB61_USS_Iowa_BB61_broadside_USN.jpg" },
            new Ship { Id = 6, Name = "USS New Jersey", ClassId = 3, CaptainId = 6, YearCommissioned = 1943, Description = "Most decorated battleship in US history.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/5/5c/New_Jersey_Sails.jpg/800px-New_Jersey_Sails.jpg" },
            new Ship { Id = 7, Name = "USS Missouri", ClassId = 3, CaptainId = 7, YearCommissioned = 1944, Description = "Site of Japanese surrender, September 1945.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/e/e8/Missouri_post_refit.JPG/800px-Missouri_post_refit.JPG" },
            new Ship { Id = 8, Name = "USS Wisconsin", ClassId = 3, CaptainId = 8, YearCommissioned = 1944, Description = "Iowa-class battleship, Korean War veteran.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/e/e0/USS_Wisconsin_%28BB-64%29_underway_at_sea%2C_circa_1988-1991_%28NH_97206-KN%29.jpg/800px-USS_Wisconsin_%28BB-64%29_underway_at_sea%2C_circa_1988-1991_%28NH_97206-KN%29.jpg" },
            new Ship { Id = 9, Name = "USS Enterprise", ClassId = 4, CaptainId = 9, YearCommissioned = 1938, Description = "The Big E, most decorated US ship of WWII.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/2/2a/USS_Enterprise_%28CV-6%29_in_Puerto_Rico%2C_early_1941.jpg/800px-USS_Enterprise_%28CV-6%29_in_Puerto_Rico%2C_early_1941.jpg" },
            new Ship { Id = 10, Name = "USS Yorktown", ClassId = 4, CaptainId = 10, YearCommissioned = 1937, Description = "Sunk at Midway after heroic resistance.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/b/bc/USS_Yorktown_%28CV-5%29_Jul1937.jpg/800px-USS_Yorktown_%28CV-5%29_Jul1937.jpg" },
            new Ship { Id = 11, Name = "USS Hornet", ClassId = 4, CaptainId = 11, YearCommissioned = 1941, Description = "Launched Doolittle Raid, sunk at Santa Cruz.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/c/cb/Aft_view_of_USS_Hornet_%28CV-8%29%2C_circa_in_late_1941_%28NH_81313%29.jpg/800px-Aft_view_of_USS_Hornet_%28CV-8%29%2C_circa_in_late_1941_%28NH_81313%29.jpg" },
            new Ship { Id = 12, Name = "USS Lexington", ClassId = 4, CaptainId = 12, YearCommissioned = 1927, Description = "Lady Lex, lost at Coral Sea.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/9/94/USS_Lexington_%28CV-2%29_leaving_San_Diego_on_14_October_1941_%2880-G-416362%29.jpg/800px-USS_Lexington_%28CV-2%29_leaving_San_Diego_on_14_October_1941_%2880-G-416362%29.jpg" },
            new Ship { Id = 13, Name = "USS Saratoga", ClassId = 4, CaptainId = 13, YearCommissioned = 1927, Description = "Survived multiple torpedo hits.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/7/7f/USS_Saratoga_%28CV-3%29_underway%2C_circa_in_1942_%2880-G-K-459%29.jpg/800px-USS_Saratoga_%28CV-3%29_underway%2C_circa_in_1942_%2880-G-K-459%29.jpg" },
            new Ship { Id = 14, Name = "HMS Hood", ClassId = 7, CaptainId = 14, YearCommissioned = 1920, Description = "Pride of the Royal Navy, sunk by Bismarck.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/3/31/HMS_Hood_%2851%29_-_March_17%2C_1924.jpg/800px-HMS_Hood_%2851%29_-_March_17%2C_1924.jpg" },
            new Ship { Id = 15, Name = "HMS Illustrious", ClassId = 5, CaptainId = 15, YearCommissioned = 1940, Description = "Armored carrier, survived multiple attacks.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/e/ed/HMS_Illustrious_%28ca._1954%29_%2820921205028%29.jpg/800px-HMS_Illustrious_%28ca._1954%29_%2820921205028%29.jpg" },
            new Ship { Id = 16, Name = "HMS Ark Royal", ClassId = 5, CaptainId = 16, YearCommissioned = 1938, Description = "Sank U-39, crippled Bismarck.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/f/f1/HMS_Ark_Royal_h79167.jpg/800px-HMS_Ark_Royal_h79167.jpg" },
            new Ship { Id = 17, Name = "HMS Victorious", ClassId = 5, CaptainId = 17, YearCommissioned = 1941, Description = "Participated in sinking Bismarck.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/4/4b/HMS_Victorious_%28R38%29_aerial_c1959.jpeg/800px-HMS_Victorious_%28R38%29_aerial_c1959.jpeg" },
            new Ship { Id = 18, Name = "HMS Formidable", ClassId = 5, CaptainId = 18, YearCommissioned = 1940, Description = "Mediterranean and Pacific service.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/c/ca/HMS_Formidable_underway_in_1942.jpg/800px-HMS_Formidable_underway_in_1942.jpg" },
            new Ship { Id = 19, Name = "Shokaku", ClassId = 6, CaptainId = 19, YearCommissioned = 1941, Description = "Pearl Harbor attacker, sunk at Philippine Sea.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/2/2b/Japanese.aircraft.carrier.zuikaku.jpg/800px-Japanese.aircraft.carrier.zuikaku.jpg" },
            new Ship { Id = 20, Name = "Zuikaku", ClassId = 6, CaptainId = 20, YearCommissioned = 1941, Description = "Last surviving Pearl Harbor carrier.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/2/2b/Japanese.aircraft.carrier.zuikaku.jpg/800px-Japanese.aircraft.carrier.zuikaku.jpg" },
            new Ship { Id = 21, Name = "HMS Exeter", ClassId = 7, CaptainId = 21, YearCommissioned = 1931, Description = "Battle of River Plate veteran.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/0/0e/HMS_Exeter_%2868%29_off_Coco_Solo_c1939.jpg/800px-HMS_Exeter_%2868%29_off_Coco_Solo_c1939.jpg" },
            new Ship { Id = 22, Name = "USS Indianapolis", ClassId = 8, CaptainId = 22, YearCommissioned = 1932, Description = "Delivered atomic bomb components.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/a/a9/USS_Indianapolis_%28CA-35%29_underway_at_sea_on_27_September_1939_%2880-G-425615%29.jpg/800px-USS_Indianapolis_%28CA-35%29_underway_at_sea_on_27_September_1939_%2880-G-425615%29.jpg" },
            new Ship { Id = 23, Name = "USS Baltimore", ClassId = 8, CaptainId = 23, YearCommissioned = 1943, Description = "Heavy cruiser, Pacific campaigns.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/9/92/USS_Baltimore_%28CA-68%29_off_the_Mare_Island_Naval_Shipyard_on_18_October_1944_%28NH_91462%29_%28cropped%29.jpg/800px-USS_Baltimore_%28CA-68%29_off_the_Mare_Island_Naval_Shipyard_on_18_October_1944_%28NH_91462%29_%28cropped%29.jpg" },
            new Ship { Id = 24, Name = "USS Fletcher", ClassId = 9, CaptainId = 24, YearCommissioned = 1942, Description = "Lead ship of famed destroyer class.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/0/06/USS_Fletcher_%28DD-445%29_underway_at_sea%2C_circa_the_1960s_%28NH_68912%29.jpg/800px-USS_Fletcher_%28DD-445%29_underway_at_sea%2C_circa_the_1960s_%28NH_68912%29.jpg" },
            new Ship { Id = 25, Name = "USS Johnston", ClassId = 9, CaptainId = 25, YearCommissioned = 1943, Description = "Heroic sacrifice at Samar.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/8/82/USS_Johnston_%28DD-557%29_underway_on_27_October_1943_%28NH_63495%29.jpg/800px-USS_Johnston_%28DD-557%29_underway_on_27_October_1943_%28NH_63495%29.jpg" },
            new Ship { Id = 26, Name = "USS Hoel", ClassId = 9, CaptainId = 1, YearCommissioned = 1943, Description = "Destroyer escort at Leyte Gulf.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/7/7b/USS_Hoel_%28DD-533%29_off_San_Francisco%2C_3_August_1943.jpg/800px-USS_Hoel_%28DD-533%29_off_San_Francisco%2C_3_August_1943.jpg" },
            new Ship { Id = 27, Name = "HMS Cossack", ClassId = 10, CaptainId = 2, YearCommissioned = 1938, Description = "Altmark incident, rescued prisoners.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/0/0f/HMS_Cossack.jpg/800px-HMS_Cossack.jpg" },
            new Ship { Id = 28, Name = "HMS Warspite", ClassId = 7, CaptainId = 3, YearCommissioned = 1915, Description = "Grand Old Lady, Jutland veteran.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/c/c0/HMS_Warspite%2C_Indian_Ocean_1942.jpg/800px-HMS_Warspite%2C_Indian_Ocean_1942.jpg" },
            new Ship { Id = 29, Name = "HMS Prince of Wales", ClassId = 3, CaptainId = 4, YearCommissioned = 1941, Description = "Sunk with Repulse off Malaya.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/f/f9/HMS_Prince_Of_Wales_in_Singapore.jpg/800px-HMS_Prince_Of_Wales_in_Singapore.jpg" },
            new Ship { Id = 30, Name = "HMS Repulse", ClassId = 7, CaptainId = 5, YearCommissioned = 1916, Description = "Battlecruiser, lost with Prince of Wales.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/7/77/Renown-7.jpg/800px-Renown-7.jpg" },
            new Ship { Id = 31, Name = "USS Wasp", ClassId = 4, CaptainId = 6, YearCommissioned = 1940, Description = "Sunk at Guadalcanal.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/8/85/USS_Wasp_%28CV-7%29_entering_Hampton_Roads_on_26_May_1942.jpg/800px-USS_Wasp_%28CV-7%29_entering_Hampton_Roads_on_26_May_1942.jpg" },
            new Ship { Id = 32, Name = "USS Ranger", ClassId = 4, CaptainId = 7, YearCommissioned = 1934, Description = "First US carrier designed as such.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/6/6f/USS_Ranger_%28CV-4%29_underway_at_sea_during_the_later_1930s.jpg/800px-USS_Ranger_%28CV-4%29_underway_at_sea_during_the_later_1930s.jpg" },
            new Ship { Id = 33, Name = "Akagi", ClassId = 6, CaptainId = 8, YearCommissioned = 1927, Description = "Flagship at Pearl Harbor, sunk at Midway.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/7/7b/AkagiDeckApril42.jpg/800px-AkagiDeckApril42.jpg" },
            new Ship { Id = 34, Name = "Kaga", ClassId = 6, CaptainId = 9, YearCommissioned = 1928, Description = "Converted carrier, sunk at Midway.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/0/0e/Japanese_Navy_Aircraft_Carrier_Kaga.jpg/800px-Japanese_Navy_Aircraft_Carrier_Kaga.jpg" },
            new Ship { Id = 35, Name = "Soryu", ClassId = 6, CaptainId = 10, YearCommissioned = 1937, Description = "Pearl Harbor carrier, sunk at Midway.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/0/0e/Japanese_Navy_Aircraft_Carrier_Kaga.jpg/800px-Japanese_Navy_Aircraft_Carrier_Kaga.jpg" },
            new Ship { Id = 36, Name = "Hiryu", ClassId = 6, CaptainId = 11, YearCommissioned = 1939, Description = "Last Japanese carrier at Midway.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/2/2b/Japanese.aircraft.carrier.zuikaku.jpg/800px-Japanese.aircraft.carrier.zuikaku.jpg" },
            new Ship { Id = 37, Name = "Scharnhorst", ClassId = 1, CaptainId = 12, YearCommissioned = 1939, Description = "German battlecruiser, North Cape.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/3/34/Bundesarchiv_DVM_10_Bild-23-63-07%2C_Schlachtschiff_%22Scharnhorst%22.jpg/800px-Bundesarchiv_DVM_10_Bild-23-63-07%2C_Schlachtschiff_%22Scharnhorst%22.jpg" },
            new Ship { Id = 38, Name = "Gneisenau", ClassId = 1, CaptainId = 13, YearCommissioned = 1938, Description = "Sister to Scharnhorst.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/9/98/Bundesarchiv_DVM_10_Bild-23-63-21%2C_Schlachtschiff_%22Gneisenau%22.jpg/800px-Bundesarchiv_DVM_10_Bild-23-63-21%2C_Schlachtschiff_%22Gneisenau%22.jpg" },
            new Ship { Id = 39, Name = "Admiral Graf Spee", ClassId = 8, CaptainId = 14, YearCommissioned = 1936, Description = "Pocket battleship, scuttled at Montevideo.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/b/b0/Bundesarchiv_DVM_10_Bild-23-63-06%2C_Panzerschiff_%22Admiral_Graf_Spee%22.jpg/800px-Bundesarchiv_DVM_10_Bild-23-63-06%2C_Panzerschiff_%22Admiral_Graf_Spee%22.jpg" },
            new Ship { Id = 40, Name = "USS San Francisco", ClassId = 8, CaptainId = 15, YearCommissioned = 1934, Description = "Heavy cruiser, Guadalcanal hero.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/d/d9/USS_San_Francisco_%28CA-38%29_off_the_Mare_Island_Naval_Shipyard_on_13_October_1944_%2819-N-73588%29.jpg/800px-USS_San_Francisco_%28CA-38%29_off_the_Mare_Island_Naval_Shipyard_on_13_October_1944_%2819-N-73588%29.jpg" },
            new Ship { Id = 41, Name = "USS Helena", ClassId = 8, CaptainId = 16, YearCommissioned = 1939, Description = "Sunk at Kula Gulf.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/6/65/USS_Helena_NH_95812.jpg/800px-USS_Helena_NH_95812.jpg" },
            new Ship { Id = 42, Name = "HMS Sheffield", ClassId = 7, CaptainId = 17, YearCommissioned = 1937, Description = "Town class cruiser.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/3/3e/HMS_Sheffield.jpg/800px-HMS_Sheffield.jpg" },
            new Ship { Id = 43, Name = "USS Washington", ClassId = 3, CaptainId = 18, YearCommissioned = 1941, Description = "Sank Kirishima at Guadalcanal.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/2/22/USS_Washington_%28BB-56%29_in_Puget_Sound%2C_10_September_1945.jpg/800px-USS_Washington_%28BB-56%29_in_Puget_Sound%2C_10_September_1945.jpg" },
            new Ship { Id = 44, Name = "USS South Dakota", ClassId = 3, CaptainId = 19, YearCommissioned = 1942, Description = "Battleship X, Guadalcanal.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/4/40/USS_South_Dakota_%28BB-57%29_off_the_Norfolk_Naval_Shipyard_on_20_August_1943_%28NH_97264%29.jpg/800px-USS_South_Dakota_%28BB-57%29_off_the_Norfolk_Naval_Shipyard_on_20_August_1943_%28NH_97264%29.jpg" },
            new Ship { Id = 45, Name = "USS North Carolina", ClassId = 3, CaptainId = 20, YearCommissioned = 1941, Description = "First new US battleship since 1920s.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/7/7f/USS_North_Carolina_NYNY_11306-6-46.jpg/800px-USS_North_Carolina_NYNY_11306-6-46.jpg" },
            new Ship { Id = 46, Name = "HMS King George V", ClassId = 3, CaptainId = 21, YearCommissioned = 1940, Description = "British battleship, Bismarck chase.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/c/c8/King_George_V_class_battleship_1945.jpg/800px-King_George_V_class_battleship_1945.jpg" },
            new Ship { Id = 47, Name = "HMS Rodney", ClassId = 3, CaptainId = 22, YearCommissioned = 1927, Description = "16-inch guns, finished Bismarck.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/8/81/HMS_Rodney_after_refitting_at_Liverpool.jpg/800px-HMS_Rodney_after_refitting_at_Liverpool.jpg" },
            new Ship { Id = 48, Name = "USS Essex", ClassId = 4, CaptainId = 23, YearCommissioned = 1942, Description = "Lead of Essex class, 20 battle stars.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/5/53/USS_Essex_CVS-9_June_1967.jpg/800px-USS_Essex_CVS-9_June_1967.jpg" },
            new Ship { Id = 49, Name = "USS Intrepid", ClassId = 4, CaptainId = 24, YearCommissioned = 1943, Description = "Survived multiple kamikaze hits.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/2/23/USS_Intrepid_%28CVS-11%29_underway_in_the_South_China_Sea_on_17_October_1968_%28NNMA.1996.488.244.058%29.jpg/800px-USS_Intrepid_%28CVS-11%29_underway_in_the_South_China_Sea_on_17_October_1968_%28NNMA.1996.488.244.058%29.jpg" },
            new Ship { Id = 50, Name = "USS Franklin", ClassId = 4, CaptainId = 25, YearCommissioned = 1944, Description = "Heavily damaged, survived to tell.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/9/98/USS_Franklin_%28CV-13%29_in_the_Elizabeth_River%2C_off_Norfolk%2C_Virginia_%28USA%29%2C_21_February_1944_%2880-G-224596%29.jpg/800px-USS_Franklin_%28CV-13%29_in_the_Elizabeth_River%2C_off_Norfolk%2C_Virginia_%28USA%29%2C_21_February_1944_%2880-G-224596%29.jpg" },
            new Ship { Id = 51, Name = "USS Gambier Bay", ClassId = 4, CaptainId = 1, YearCommissioned = 1943, Description = "Escort carrier, sunk at Leyte Gulf.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/2/2e/USS_Gambier_Bay_%28CVE-73%29_underway%2C_circa_1944_%28NH_95699%29.jpg/800px-USS_Gambier_Bay_%28CVE-73%29_underway%2C_circa_1944_%28NH_95699%29.jpg" },
            new Ship { Id = 52, Name = "HMS Hermes", ClassId = 5, CaptainId = 2, YearCommissioned = 1924, Description = "First purpose-built carrier, sunk off Ceylon.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/4/4e/HMS_Hermes_%2895%29.jpg/800px-HMS_Hermes_%2895%29.jpg" },
            new Ship { Id = 53, Name = "USS Quincy", ClassId = 8, CaptainId = 3, YearCommissioned = 1936, Description = "Heavy cruiser, lost at Savo Island.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/5/5a/USS_Quincy_%28CA-39%29_off_the_Mare_Island_Naval_Shipyard_on_12_September_1940.jpg/800px-USS_Quincy_%28CA-39%29_off_the_Mare_Island_Naval_Shipyard_on_12_September_1940.jpg" },
            new Ship { Id = 54, Name = "USS Vincennes", ClassId = 8, CaptainId = 4, YearCommissioned = 1937, Description = "Heavy cruiser, lost at Savo Island.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/8/8a/USS_Vincennes_%28CA-44%29_underway_on_14_May_1942.jpg/800px-USS_Vincennes_%28CA-44%29_underway_on_14_May_1942.jpg" },
            new Ship { Id = 55, Name = "HMS Ajax", ClassId = 7, CaptainId = 5, YearCommissioned = 1935, Description = "River Plate veteran with Exeter.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/6/6d/HMS_Ajax_%2822%29.jpg/800px-HMS_Ajax_%2822%29.jpg" },
            new Ship { Id = 56, Name = "HMS Achilles", ClassId = 7, CaptainId = 6, YearCommissioned = 1933, Description = "New Zealand cruiser, River Plate.", ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/0/0e/HMS_Achilles_%2870%29_1930s.jpg/800px-HMS_Achilles_%2870%29_1930s.jpg" }
        };

        modelBuilder.Entity<Ship>().HasData(ships);
    }
}
