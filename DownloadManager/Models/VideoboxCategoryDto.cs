using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DownloadManager.Models;

public class VideoboxCategoryDto
{
    public int id { get; set; }
    public string name { get; set; } = string.Empty;
    
    // "class" è una parola chiave in C#, usiamo @class per il nome della proprietà
    [JsonPropertyName("class")]
    public string @class { get; set; } = string.Empty;
    
    public VideoboxPermissionsDto? permissions { get; set; }
}

public class VideoboxPermissionsDto
{
    public string? perm_2 { get; set; }
}

public class VideoboxCategoryPagedDto
{
    public int page { get; set; }
    public int perPage { get; set; }
    public int totalResults { get; set; }
    public int totalPages { get; set; }
    public List<VideoboxCategoryDto> results { get; set; } = new();
}