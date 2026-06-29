using System.Text.Json;
using System.Text.RegularExpressions;

namespace AshServer.Agent;

// ── Tool definitions ──────────────────────────────────────────────────────────

public static class AgentTools
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static readonly JsonElement ToolDefinitions = JsonSerializer.Deserialize<JsonElement>("""
        [
          {
            "type": "function",
            "function": {
              "name": "web_search",
              "description": "Search the web for current information. Returns a list of results with titles, URLs, and snippets.",
              "parameters": {
                "type": "object",
                "properties": {
                  "query": { "type": "string", "description": "The search query" }
                },
                "required": ["query"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "fetch_url",
              "description": "Fetch and read the content of a URL. Returns the page text.",
              "parameters": {
                "type": "object",
                "properties": {
                  "url": { "type": "string", "description": "The URL to fetch" }
                },
                "required": ["url"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "calculate",
              "description": "Evaluate a mathematical expression and return the result.",
              "parameters": {
                "type": "object",
                "properties": {
                  "expression": { "type": "string", "description": "Math expression to evaluate, e.g. '2 + 2 * 10'" }
                },
                "required": ["expression"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "get_time",
              "description": "Get the current date and time.",
              "parameters": { "type": "object", "properties": {} }
            }
          }
        ]
        """);

    private static readonly HashSet<string> BuiltinToolNames =
        ["web_search", "fetch_url", "calculate", "get_time"];

    public static bool IsBuiltinTool(string name) => BuiltinToolNames.Contains(name);

    public static async Task<string> Execute(string toolName, JsonElement args)
    {
        return toolName switch
        {
            "web_search" => await WebSearch(args.TryGetProperty("query", out var q) ? q.GetString()! : ""),
            "fetch_url"  => await FetchUrl(args.TryGetProperty("url", out var u) ? u.GetString()! : ""),
            "calculate"  => Calculate(args.TryGetProperty("expression", out var e) ? e.GetString()! : ""),
            "get_time"   => GetTime(),
            _            => $"Unknown tool: {toolName}"
        };
    }

    private static async Task<string> WebSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "No query provided.";
        try
        {
            var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (compatible; AshServer/1.0)");
            var resp = await Http.SendAsync(req);
            var html = await resp.Content.ReadAsStringAsync();

            // Extract result links and snippets via regex
            var links = Regex.Matches(html, @"<a[^>]+class=""result-link""[^>]*href=""([^""]+)""[^>]*>([^<]+)</a>");
            var snippets = Regex.Matches(html, @"<td[^>]+class=""result-snippet""[^>]*>(.*?)</td>", RegexOptions.Singleline);

            var results = new List<string>();
            for (int i = 0; i < Math.Min(links.Count, 5); i++)
            {
                var href = links[i].Groups[1].Value;
                var title = Regex.Replace(links[i].Groups[2].Value, "<[^>]+>", "").Trim();
                var snippet = i < snippets.Count
                    ? Regex.Replace(snippets[i].Groups[1].Value, "<[^>]+>", "").Trim()
                    : "";
                results.Add($"[{i + 1}] {title}\n    URL: {href}\n    {snippet}");
            }

            return results.Count > 0
                ? $"Search results for \"{query}\":\n\n" + string.Join("\n\n", results)
                : $"No results found for \"{query}\"";
        }
        catch (Exception ex)
        {
            return $"Search failed: {ex.Message}";
        }
    }

    private static async Task<string> FetchUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "No URL provided.";
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            req.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            
            var resp = await Http.SendAsync(req);
            var html = await resp.Content.ReadAsStringAsync();

            // 1. Remove non-content blocks (scripts, styles, head, header, footer, nav)
            html = Regex.Replace(html, @"<(script|style|head|header|footer|nav)\b[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline); // comments

            // 2. Convert structural elements to Markdown
            html = Regex.Replace(html, @"<h1\b[^>]*>(.*?)</h1>", "\n# $1\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<h2\b[^>]*>(.*?)</h2>", "\n## $1\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<h3\b[^>]*>(.*?)</h3>", "\n### $1\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<p\b[^>]*>(.*?)</p>", "\n$1\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<li\b[^>]*>(.*?)</li>", "\n* $1", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            
            // Convert links: <a href="url">text</a> -> [text](url)
            html = Regex.Replace(html, @"<a\b[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", "[$2]($1)", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            // 3. Strip remaining HTML tags
            var text = Regex.Replace(html, "<[^>]+>", " ");
            
            // 4. Decode HTML entities and clean whitespace
            text = System.Net.WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"[ \t]+", " "); // collapse horizontal spaces
            text = Regex.Replace(text, @"\r\n|\n|\r", "\n"); // normalize newlines
            text = Regex.Replace(text, @"\n{3,}", "\n\n"); // collapse multiple newlines
            text = text.Trim();

            // Return up to 6000 characters for more context, with a truncation note
            return text.Length > 6000 ? text[..6000] + "\n\n[Content truncated for length...]" : text;
        }
        catch (Exception ex)
        {
            return $"Fetch failed: {ex.Message}";
        }
    }

    private static string Calculate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "No expression provided.";
        try
        {
            // Only allow safe math characters
            if (!Regex.IsMatch(expression, @"^[\d\s\+\-\*\/\.\(\)\%\^]+$"))
                return "Expression contains invalid characters.";
            var result = new System.Data.DataTable().Compute(expression, null);
            return $"{expression} = {result}";
        }
        catch (Exception ex)
        {
            return $"Calculation failed: {ex.Message}";
        }
    }

    private static string GetTime() =>
        $"Current date and time: {DateTime.Now:dddd, MMMM d, yyyy h:mm tt} (local)";
}
