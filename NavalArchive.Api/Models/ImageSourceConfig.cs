namespace NavalArchive.Api.Models;

/// <summary>Configurable image source for populate/search. Built-in (Pexels, etc.) or custom API.</summary>
public class ImageSourceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Pexels | Pixabay | Unsplash | Google | Wikipedia | Custom</summary>
    public string ProviderType { get; set; } = "";
    public int RetryCount { get; set; } = 2;
    public int SortOrder { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Maps to key: PEXELS_API_KEY, PIXABAY_API_KEY, etc. Google uses GOOGLE_API_KEY + GOOGLE_CSE_ID</summary>
    public string? AuthKeyRef { get; set; }
    public CustomApiConfig? CustomConfig { get; set; }
}

/// <summary>Config for custom API sources. Maps auth and response fields to required values.</summary>
public class CustomApiConfig
{
    public string BaseUrl { get; set; } = "";
    public string QueryParam { get; set; } = "q";
    public string AuthType { get; set; } = "none"; // "header" | "query" | "none"
    public string? AuthHeaderName { get; set; }
    public string? AuthQueryParam { get; set; }
    public string? AuthValueFromKey { get; set; }
    public string ResponsePath { get; set; } = "results";
    public string ImageUrlPath { get; set; } = "";
}
