using System.Text.Json.Serialization;

namespace BatchConvertToCHD.Models;

/// <summary>
///     Represents the structure of a GitHub release JSON response.
///     Properties are public for System.Text.Json deserialization.
/// </summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;

    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }

    [JsonPropertyName("draft")] public bool Draft { get; set; }
}