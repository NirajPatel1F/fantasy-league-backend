using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using HtmlAgilityPack;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// Configure Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 1. Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// 2. Configure Super-Stealth HTTP Client (Bypasses Cloudflare 'Access Denied')
builder.Services.AddHttpClient("ScraperClient", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
    client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    client.DefaultRequestHeaders.Add("Connection", "keep-alive");
    client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
    client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
    client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
    client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
    client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Cloudflare blocks clients that don't support modern compression while claiming to be Chrome
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
    UseCookies = true,
    AllowAutoRedirect = true
});

// 3. Configure SQLite
builder.Services.AddDbContext<FantasyDbContext>(options =>
    options.UseSqlite("Data Source=fantasy.db"));

var app = builder.Build();

// Enable Swagger UI
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("AllowAll");

// 4. Auto-Migrate and Seed Database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FantasyDbContext>();
    db.Database.EnsureCreated();
    SeedDatabase(db);
}

// Global JSON Options to ensure React reads camelCase properly
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// ==========================================
// API ENDPOINTS
// ==========================================

app.MapGet("/api/settings", async (FantasyDbContext db) =>
{
    var settings = await db.Settings.FirstOrDefaultAsync() ?? new Setting();
    return Results.Ok(settings);
});

app.MapPost("/api/settings", async (Setting updatedSettings, FantasyDbContext db) =>
{
    var settings = await db.Settings.FirstOrDefaultAsync();
    if (settings == null) db.Settings.Add(updatedSettings);
    else
    {
        settings.ApiKey = updatedSettings.ApiKey;
        settings.SeriesId = updatedSettings.SeriesId;
    }
    await db.SaveChangesAsync();
    return Results.Ok(settings);
});

app.MapGet("/api/teams", async (FantasyDbContext db) =>
{
    var teams = await db.Teams.Include(t => t.Players).ToListAsync();
    var allStats = await db.PlayerMatchStats.ToListAsync();

    var result = teams.Select(t => {
        double teamTotal = 0;
        var processedPlayers = t.Players.Select(p => {
            
            var matchedLogs = allStats
                .Where(stat => IsMatch(p.Name, stat.PlayerName))
                .GroupBy(stat => stat.MatchId)
                .Select(g => g.First())
                .ToList();

            var basePts = matchedLogs.Sum(l => l.Points);
            var finalPts = basePts * p.Multiplier;
            teamTotal += finalPts;

            double totalBat = 0, totalBowl = 0, totalField = 0, totalBonus = 0, totalPenalty = 0;
            foreach(var l in matchedLogs)
            {
                if (!string.IsNullOrEmpty(l.BreakdownJson))
                {
                    try
                    {
                        var bNode = JsonNode.Parse(l.BreakdownJson);
                        if (bNode != null)
                        {
                            totalBat += bNode["bat"]?.GetValue<double>() ?? 0;
                            totalBowl += bNode["bowl"]?.GetValue<double>() ?? 0;
                            totalField += bNode["field"]?.GetValue<double>() ?? 0;
                            totalBonus += bNode["bonus"]?.GetValue<double>() ?? 0;
                            totalPenalty += bNode["penalty"]?.GetValue<double>() ?? 0;
                        }
                    }
                    catch { }
                }
            }

            return new {
                name = p.Name,
                role = p.Role,
                multiplier = p.Multiplier,
                points = finalPts,
                basePoints = basePts,
                breakdown = new {
                    bat = totalBat,
                    bowl = totalBowl,
                    field = totalField,
                    bonus = totalBonus,
                    penalty = totalPenalty
                }
            };
        }).OrderByDescending(p => p.points).ToList();

        return new {
            owner = t.Owner,
            totalPoints = Math.Round(teamTotal, 1),
            players = processedPlayers
        };
    }).OrderByDescending(t => t.totalPoints).ToList();

    return Results.Json(result, jsonOpts);
});

app.MapGet("/api/players/{playerName}/matches", async (string playerName, FantasyDbContext db) =>
{
    var allStats = await db.PlayerMatchStats.ToListAsync();
    
    var matchedLogs = allStats
        .Where(stat => IsMatch(playerName, stat.PlayerName))
        .GroupBy(stat => stat.MatchId)
        .Select(g => g.First())
        .Select(l => new {
            matchName = l.MatchName,
            pts = l.Points,
            stats = JsonSerializer.Deserialize<object>(l.StatsJson, jsonOpts),
            breakdown = JsonSerializer.Deserialize<object>(l.BreakdownJson, jsonOpts)
        })
        .ToList();

    return Results.Json(matchedLogs, jsonOpts);
});

// POST Sync Live Data (The ESPN Cricinfo Scraper Engine)
app.MapPost("/api/sync", async (FantasyDbContext db, IHttpClientFactory httpClientFactory) =>
{
    var settings = await db.Settings.FirstOrDefaultAsync();
    var inputUrl = settings?.SeriesId?.Trim();
    
    if (string.IsNullOrEmpty(inputUrl))
        return Results.BadRequest("Please paste a Cricinfo 'Match Results' URL or a comma-separated list of 'full-scorecard' URLs into the Series ID field.");

    // Using the heavily configured "ScraperClient" with Brotli/Gzip support
    var client = httpClientFactory.CreateClient("ScraperClient");

    // Helper function to safely fetch HTML directly OR via a proxy if blocked
    async Task<string> GetHtmlAsync(string targetUrl)
    {
        // Strict Cloudflare check: look for Access Denied or Just a moment pages
        bool IsValidHtml(string? h) => !string.IsNullOrEmpty(h) 
            && !h.Contains("<title>Just a moment...</title>") 
            && !h.Contains("Access Denied") 
            && !h.ToLower().Contains("cloudflare");

        // Strategy 1: Direct Request (Now highly effective due to DecompressionMethods in client)
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, targetUrl) { Version = new Version(1, 1) };
            var res = await client.SendAsync(req);
            if (res.IsSuccessStatusCode)
            {
                var html = await res.Content.ReadAsStringAsync();
                if (IsValidHtml(html)) return html;
            }
        }
        catch { }

        // Strategy 2: AllOrigins RAW (Returns pure HTML, bypassing CORS/Cloudflare)
        try
        {
            var proxyUrl = $"https://api.allorigins.win/raw?url={Uri.EscapeDataString(targetUrl)}";
            var proxyRes = await client.GetAsync(proxyUrl);
            if (proxyRes.IsSuccessStatusCode)
            {
                var html = await proxyRes.Content.ReadAsStringAsync();
                if (IsValidHtml(html)) return html;
            }
        }
        catch { }

        // Strategy 3: CodeTabs Proxy
        try
        {
            var proxyUrl = $"https://api.codetabs.com/v1/proxy?quest={Uri.EscapeDataString(targetUrl)}";
            var proxyRes = await client.GetAsync(proxyUrl);
            if (proxyRes.IsSuccessStatusCode)
            {
                var html = await proxyRes.Content.ReadAsStringAsync();
                if (IsValidHtml(html)) return html;
            }
        }
        catch { }

        throw new Exception($"Failed to bypass bot protection for: {targetUrl}. Connection was explicitly denied by Cloudflare.");
    }

    var matchUrls = new HashSet<string>();

    // Support both direct scorecard links AND series result pages
    if (inputUrl.Contains(","))
    {
        foreach (var url in inputUrl.Split(','))
        {
            if (url.Contains("full-scorecard")) matchUrls.Add(url.Trim());
        }
    }
    else
    {
        try 
        {
            var html = await GetHtmlAsync(inputUrl);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            
            var links = doc.DocumentNode.SelectNodes("//a[@href]");
            if (links != null)
            {
                foreach (var link in links)
                {
                    var href = link.GetAttributeValue("href", "");
                    
                    // Match links like /series/tournament-name/match-name-12345/anything
                    var matchRegex = Regex.Match(href, @"/series/([^/]+)/([^/]+-(\d+))(?:/.*)?$");
                    if (matchRegex.Success)
                    {
                        var seriesSlug = matchRegex.Groups[1].Value;
                        var matchSlug = matchRegex.Groups[2].Value;
                        
                        // Exclude non-match links. Matches usually have "vs", "match", or "final"
                        if (matchSlug.Contains("match") || matchSlug.Contains("vs") || matchSlug.Contains("final") || matchSlug.Contains("qualifier")) 
                        {
                            matchUrls.Add($"https://www.espncricinfo.com/series/{seriesSlug}/{matchSlug}/full-scorecard");
                        }
                    }
                }
            }

            // Super Fallback: If links are completely hidden in React/Next.js JSON state
            if (!matchUrls.Any())
            {
                var rawMatches = Regex.Matches(html, @"/series/([^/\""']+)/([^/\""']+-(\d+))/full-scorecard");
                foreach (Match m in rawMatches)
                {
                    matchUrls.Add($"https://www.espncricinfo.com{m.MatchId}");
                }
            }
        } 
        catch (Exception ex)
        {
            return Results.BadRequest($"Failed to load ESPN Cricinfo URL. Error: {ex.Message}");
        }
    }

    if (!matchUrls.Any()) 
        return Results.BadRequest("Could not find any scorecards. Cricinfo might be aggressively blocking the connection, try pasting direct 'full-scorecard' URLs separated by commas instead.");

    var existingMatchIds = await db.Matches.Select(m => m.MatchId).ToListAsync();
    int syncedCount = 0;

    foreach (var url in matchUrls)
    {
        // Extract a unique match ID from the URL using Regex (e.g., ...1st-match-1422119/full-scorecard)
        var matchIdMatch = Regex.Match(url, @"-([0-9]+)/full-scorecard");
        var matchId = matchIdMatch.Success ? matchIdMatch.Groups[1].Value : url;

        if (existingMatchIds.Contains(matchId)) continue;

        HtmlDocument matchDoc = new HtmlDocument();
        try 
        { 
            // Use the robust helper for individual scorecards too
            var matchHtml = await GetHtmlAsync(url);
            matchDoc.LoadHtml(matchHtml);
        } 
        catch { continue; }

        var playersInMatch = new Dictionary<string, RawStats>();
        RawStats GetPlayer(string rawName)
        {
            rawName = rawName.Replace("(c)", "").Replace("†", "").Replace("&dagger;", "").Replace("(c)†", "").Trim();
            if (!playersInMatch.ContainsKey(rawName)) playersInMatch[rawName] = new RawStats { Name = rawName };
            return playersInMatch[rawName];
        }

        // --- SCRAPE BATTING TABLES ---
        var battingTables = matchDoc.DocumentNode.SelectNodes("//table[.//th[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'batt')]]");
        if (battingTables != null)
        {
            foreach (var table in battingTables)
            {
                // Find column indices dynamically just in case ESPN changes their table layout!
                int rIdx = 2, bIdx = 3, foursIdx = 5, sixesIdx = 6;
                var headers = table.SelectNodes(".//th");
                if (headers != null) {
                    for(int i=0; i<headers.Count; i++) {
                        var txt = headers[i].InnerText.Trim().ToUpper();
                        if (txt == "R") rIdx = i;
                        else if (txt == "B") bIdx = i;
                        else if (txt == "4S") foursIdx = i;
                        else if (txt == "6S") sixesIdx = i;
                    }
                }

                var rows = table.SelectNodes(".//tr[td]");
                if (rows == null) continue;
                foreach (var row in rows)
                {
                    var tds = row.SelectNodes("td");
                    if (tds != null && tds.Count > Math.Max(rIdx, sixesIdx))
                    {
                        var name = tds[0].InnerText.Trim();
                        if (string.IsNullOrEmpty(name) || name.ToLower().Contains("extras") || name.ToLower().Contains("total") || name.ToLower().Contains("did not bat")) continue;

                        var p = GetPlayer(name);
                        p.Dismissal = tds[1].InnerText.Trim();
                        
                        int.TryParse(tds[rIdx].InnerText.Trim(), out int r); p.Runs += r;
                        int.TryParse(tds[bIdx].InnerText.Trim(), out int b); p.Balls += b;
                        int.TryParse(tds[foursIdx].InnerText.Trim(), out int fours); p.Fours += fours;
                        int.TryParse(tds[sixesIdx].InnerText.Trim(), out int sixes); p.Sixes += sixes;

                        // Parse Fielders & Dismissal Bonuses
                        var dis = p.Dismissal.ToLower();
                        if (dis.StartsWith("c ")) {
                            int bIdxStr = dis.IndexOf(" b ");
                            if (bIdxStr > 2) GetPlayer(dis.Substring(2, bIdxStr - 2).Trim()).Catches += 1;
                        }
                        if (dis.Contains("st ")) {
                            int stIdx = dis.IndexOf("st "); int bIdxStr = dis.IndexOf(" b ");
                            if (stIdx >= 0 && bIdxStr > stIdx + 3) GetPlayer(dis.Substring(stIdx + 3, bIdxStr - stIdx - 3).Trim()).Stumpings += 1;
                        }
                        if (dis.Contains("run out")) {
                            var start = dis.IndexOf('('); var end = dis.IndexOf(')');
                            if (start >= 0 && end > start) GetPlayer(dis.Substring(start + 1, end - start - 1).Split('/')[0].Trim()).Runouts += 1;
                        }
                        if (dis.Contains("lbw b ") || dis.StartsWith("b ")) {
                            int bIdxStr = dis.LastIndexOf(" b ");
                            if (bIdxStr >= 0) GetPlayer(dis.Substring(bIdxStr + 3).Trim()).LbwBowled += 1;
                        }
                    }
                }
            }
        }

        // --- SCRAPE BOWLING TABLES ---
        var bowlingTables = matchDoc.DocumentNode.SelectNodes("//table[.//th[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'bowl')]]");
        if (bowlingTables != null)
        {
            foreach (var table in bowlingTables)
            {
                int oIdx = 1, mIdx = 2, rIdx = 3, wIdx = 4;
                var headers = table.SelectNodes(".//th");
                if (headers != null) {
                    for(int i=0; i<headers.Count; i++) {
                        var txt = headers[i].InnerText.Trim().ToUpper();
                        if (txt == "O") oIdx = i;
                        else if (txt == "M") mIdx = i;
                        else if (txt == "R") rIdx = i;
                        else if (txt == "W") wIdx = i;
                    }
                }

                var rows = table.SelectNodes(".//tr[td]");
                if (rows == null) continue;
                foreach (var row in rows)
                {
                    var tds = row.SelectNodes("td");
                    if (tds != null && tds.Count > Math.Max(oIdx, wIdx))
                    {
                        var name = tds[0].InnerText.Trim();
                        if (string.IsNullOrEmpty(name) || name.ToLower().Contains("extras") || name.ToLower().Contains("total")) continue;

                        var p = GetPlayer(name);
                        double.TryParse(tds[oIdx].InnerText.Trim(), out double o); p.Overs += o;
                        int.TryParse(tds[mIdx].InnerText.Trim(), out int m); p.Maidens += m;
                        int.TryParse(tds[rIdx].InnerText.Trim(), out int r); p.BowlRuns += r;
                        int.TryParse(tds[wIdx].InnerText.Trim(), out int w); p.Wickets += w;
                    }
                }
            }
        }

        // Extract Title
        var titleNode = matchDoc.DocumentNode.SelectSingleNode("//title");
        string matchName = titleNode != null ? titleNode.InnerText.Split('|')[0].Trim() : $"Match {matchId}";

        db.Matches.Add(new Match { MatchId = matchId, Name = matchName });
        existingMatchIds.Add(matchId);
        
        foreach (var kvp in playersInMatch)
        {
            var res = CalculateD11(kvp.Value);
            db.PlayerMatchStats.Add(new PlayerMatchStat
            {
                MatchId = matchId, MatchName = matchName, PlayerName = kvp.Key,
                Points = res.Total,
                StatsJson = JsonSerializer.Serialize(kvp.Value, jsonOpts),
                BreakdownJson = JsonSerializer.Serialize(res.Breakdown, jsonOpts)
            });
        }
        syncedCount++;
    }

    await db.SaveChangesAsync();
    var totalMatches = await db.Matches.CountAsync();
    
    return Results.Ok(new { synced = syncedCount, total = totalMatches, message = "Successfully scraped ESPNcricinfo scorecards!" });
});

// DELETE Clear Matches
app.MapDelete("/api/sync", async (FantasyDbContext db) =>
{
    db.Matches.RemoveRange(db.Matches);
    db.PlayerMatchStats.RemoveRange(db.PlayerMatchStats);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.Run("http://localhost:5000");

static void SeedDatabase(FantasyDbContext db)
{
    if (db.Teams.Any()) return;
    var rawData = new Dictionary<string, string>
    {
        {"DIPAM Drillers", "D. Miller, K.L. RAHUL(VC), Riyan Parag, H. Klaasen, Rahane, Dhruv Jurel, Kamindu Mendis, Shahrukh khan, Rahul tewatiya, Rinku Singh, Sunil Narine(C), R. Jadeja, KG Rabada, T. Natarajan"},
        {"PRIYANK", "Vaibhav Suryavanshi, Ishan Kishan(C), Tristan Stubbs, Ruturaj Gaikwad, Glenn Phillips, Sherfane Rutherford, Ayush Badoni, Axar Patel(VC), Azmatullah Omarzai, Washington Sundar, Venkatesh Iyer, Liam Livingstone, Vipraj Nigam, Kuldeep Yadav, Ravi bishnoi"},
        {"UTKARSH", "Tilak verma, Shimron Hetmyer(C), Tim David, Devdutt Padikkal, Nehal Wadhera, Angkrish Raghuvanshi, Nitish Kumar Reddy, Romario Shepherd(VC), Krunal Pandya, Ashutosh Sharma, Shashank Singh, Shardul Thakur, Josh Hazlewood, Yuzvendra Chahal, Noor Ahmad"},
        {"CHINTU Champions", "Jitesh Sharma, Quinton De Kock, Shubman Gill(C), Shreyas Iyer(VC), Aiden Markram, Rajat Patidar, Karun Nair, Mitchell Marsh, Rachin Ravindra, Jamie Overton, Varun Chakravarthy, Deepak Chahar, Avesh Khan, Mukesh Kumar, Ishant Sharma"},
        {"KARAN Clashers", "Rishabh Pant, Phil Salt(VC), Prabhsimran Singh, Dewald Brevis, Priyansh Arya, Rovman Powell, Hardik Pandya(C), Corbin Bosch, Arshdeep Singh, Trent Boult, Harshal Patel, Tushar Deshpande, Jaydev Unadkat, Sandeep Sharma, Mayank Markande"},
        {"VEDANSH Warriors", "Sanju Samson(VC), Ryan Rickelton, Abhishek Porel, Sai Sudharsan(C), Rohit Sharma, Ayush Mhatre, Nitish Rana, David Miller, Shivam Dube, Jacob Bethell, Sameer Rizvi, R. Sai Kishore, Digvesh Rathi, Zeeshan Ansari, Anshul Kamboj"},
        {"J.D. GUJJU TOLI", "Jos Buttler, MS Dhoni, Urvil Patel, Yashasvi Jaiswal, Cameron Green, Naman Dhir, Abhishek Sharma(C), Marco Jansen, Shahbaz Ahmed, Jasprit Bumrah(VC), Prasidh Krishna, Jofra Archer, Khaleel Ahmed"},
        {"JAY", "Nicholas Pooran, Virat Kohli(C), Travis Head(VC), Suryakumar Yadav, Marcus Stoinis, Will Jacks, Rashid Khan, Mohammad Siraj, Bhuvneshwar Kumar, Abdul samad, Umran Malik, Vaibhav Arora, Harnoor pannu, Prashant veer"},

    };
    foreach (var teamRaw in rawData) {
        var team = new Team { Owner = teamRaw.Key };
        foreach (var rawName in teamRaw.Value.Split(',').Select(p => p.Trim())) {
            var name = rawName.Replace("(C)", "").Replace("(VC)", "").Trim();
            team.Players.Add(new Player { Name = name, Role = rawName.Contains("(C)") ? "Captain" : rawName.Contains("(VC)") ? "Vice Captain" : "Player", Multiplier = rawName.Contains("(C)") ? 2.0 : rawName.Contains("(VC)") ? 1.5 : 1.0 });
        }
        db.Teams.Add(team);
    }
    db.SaveChanges();
}	


// ==========================================
// DREAM11 ENGINE LOGIC
// ==========================================

static (double Total, object Breakdown) CalculateD11(RawStats stats)
{
    double bat = 0, bowl = 0, field = 0, bonus = 0, penalty = 0;

    // Batting Points
    if (stats.Runs > 0)
    {
        bat += stats.Runs + (stats.Fours * 1) + (stats.Sixes * 2);
        if (stats.Runs >= 100) bat += 16; else if (stats.Runs >= 50) bat += 8;
        if (stats.Balls >= 10)
        {
            double sr = (stats.Runs / (double)stats.Balls) * 100;
            if (sr > 170) bonus += 6; else if (sr > 150) bonus += 4; else if (sr >= 130) bonus += 2;
            else if (sr >= 60 && sr <= 70) penalty -= 2; else if (sr >= 50 && sr < 60) penalty -= 4; else if (sr < 50) penalty -= 6;
        }
    }
    if (stats.Runs == 0 && stats.Balls > 0 && !string.IsNullOrEmpty(stats.Dismissal) && !stats.Dismissal.ToLower().Contains("not out")) penalty -= 2;

    // Bowling Points
    if (stats.Overs > 0)
    {
        bowl += (stats.Wickets * 25) + (stats.LbwBowled * 8) + (stats.Maidens * 12);
        if (stats.Wickets >= 5) bowl += 16; else if (stats.Wickets >= 4) bowl += 8; else if (stats.Wickets == 3) bowl += 4;
        if (stats.Overs >= 2)
        {
            double eco = stats.BowlRuns / stats.Overs;
            if (eco < 5) bonus += 6; else if (eco < 6) bonus += 4; else if (eco <= 7) bonus += 2;
            else if (eco >= 10 && eco <= 11) penalty -= 2; else if (eco > 11 && eco <= 12) penalty -= 4; else if (eco > 12) penalty -= 6;
        }
    }

    // Fielding
    field += (stats.Catches * 8) + (stats.Stumpings * 12) + (stats.Runouts * 12);
    if (stats.Catches >= 3) field += 4;

    return (bat + bowl + field + bonus + penalty, new { bat, bowl, field, bonus, penalty });
}

static bool IsMatch(string rosterName, string apiName)
{
    var rClean = new string(rosterName.ToLower().Where(char.IsLetterOrDigit).ToArray());
    var aClean = new string(apiName.ToLower().Where(char.IsLetterOrDigit).ToArray());
    if (rClean == aClean) return true;

    var rParts = rosterName.ToLower().Split(new[] { ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
    var aParts = apiName.ToLower().Split(new[] { ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);

    if (rParts.Length > 0 && aParts.Length > 0 && rParts.Last() == aParts.Last())
    {
        if (rParts[0] == aParts[0]) return true; 
        
        if (rParts[0].Length == 1 || aParts[0].Length == 1)
        {
            if (rParts[0][0] == aParts[0][0]) return true;
        }
    }
    return false;
}

// ==========================================
// DATA MODELS
// ==========================================

public class FantasyDbContext : DbContext
{
    public FantasyDbContext(DbContextOptions<FantasyDbContext> options) : base(options) { }
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<PlayerMatchStat> PlayerMatchStats => Set<PlayerMatchStat>();
}

public class Setting { public int Id { get; set; } public string ApiKey { get; set; } = ""; public string SeriesId { get; set; } = ""; }
public class Team { public int Id { get; set; } public string Owner { get; set; } = ""; public List<Player> Players { get; set; } = new(); }
public class Player { public int Id { get; set; } public string Name { get; set; } = ""; public string Role { get; set; } = "Player"; public double Multiplier { get; set; } = 1.0; public int TeamId { get; set; } }
public class Match { public int Id { get; set; } public string MatchId { get; set; } = ""; public string Name { get; set; } = ""; }
public class PlayerMatchStat { public int Id { get; set; } public string MatchId { get; set; } = ""; public string MatchName { get; set; } = ""; public string PlayerName { get; set; } = ""; public double Points { get; set; } public string StatsJson { get; set; } = ""; public string BreakdownJson { get; set; } = ""; }
public class RawStats { public string Name { get; set; } = ""; public int Runs { get; set; } public int Balls { get; set; } public int Fours { get; set; } public int Sixes { get; set; } public string Dismissal { get; set; } = ""; public int Wickets { get; set; } public double Overs { get; set; } public int Maidens { get; set; } public int BowlRuns { get; set; } public int LbwBowled { get; set; } public int Catches { get; set; } public int Stumpings { get; set; } public int Runouts { get; set; } }
