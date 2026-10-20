using System.Collections.Generic;

namespace DownloadManager.Models;
using System.Text.Json;

public class VideoboxDto
{
    public int id { get; set; }
    public string title { get; set; } = string.Empty;
    public string url { get; set; } = string.Empty;
    public VideoboxAuthorDto? author { get; set; }
    public JsonElement category { get; set; }
    public Dictionary<string, VideoboxServerDto> videos { get; set; } = new();
}

public class VideoboxAuthorDto
{
    public int id { get; set; }
}

public class VideoboxServerDto
{
    public int id { get; set; }
    public string vbs_name { get; set; } = string.Empty;
    public Dictionary<string, VideoboxEpisodeDto> online { get; set; } = new();
}

public class VideoboxEpisodeDto
{
    public int vbo_id { get; set; }
    public string vbo_url { get; set; } = string.Empty;
}

public class VideoboxPagedDto
{
    public int page { get; set; }
    public int perPage { get; set; }
    public int totalResults { get; set; }
    public int totalPages { get; set; }
    public List<VideoboxDto> results { get; set; } = new();
}