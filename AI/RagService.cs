using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AshServer.Data;

namespace AshServer.AI;

public class DocumentChunk
{
    public int Id { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float[]? Embedding { get; set; }
    public double Similarity { get; set; }
}

public class RagService
{
    private readonly Database _db;
    private readonly BackendManager _backends;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public RagService(Database db, BackendManager backends)
    {
        _db = db;
        _backends = backends;
    }

    public async Task IndexDocumentAsync(string filename, string content)
    {
        var documentId = Guid.NewGuid().ToString();

        // 1. Chunk the document (500 chars with 100 char overlap)
        var chunks = ChunkText(content, 500, 100);
        if (chunks.Count == 0) return;

        // 2. Generate embeddings and save each chunk
        foreach (var chunkText in chunks)
        {
            try
            {
                var embedding = await GenerateEmbeddingAsync(chunkText);
                if (embedding != null)
                {
                    await SaveChunkAsync(documentId, filename, chunkText, embedding);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[rag] Failed to index chunk of '{filename}': {ex.Message}");
            }
        }
        Console.WriteLine($"[rag] Successfully indexed document '{filename}' with {chunks.Count} chunks.");
    }

    public async Task<List<DocumentChunk>> SearchAsync(string query, string? filename = null, int limit = 4)
    {
        var queryEmbedding = await GenerateEmbeddingAsync(query);
        if (queryEmbedding == null) return new List<DocumentChunk>();

        // Get all candidate chunks from database
        var candidates = await GetChunksAsync(filename);
        
        // Calculate cosine similarity in memory
        foreach (var chunk in candidates)
        {
            if (chunk.Embedding != null)
            {
                chunk.Similarity = CosineSimilarity(queryEmbedding, chunk.Embedding);
            }
        }

        // Return top K sorted by similarity
        return candidates
            .Where(c => c.Similarity > 0.3) // threshold
            .OrderByDescending(c => c.Similarity)
            .Take(limit)
            .ToList();
    }

    private List<string> ChunkText(string text, int chunkSize, int overlap)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        int index = 0;
        while (index < text.Length)
        {
            int len = Math.Min(chunkSize, text.Length - index);
            var chunk = text.Substring(index, len);
            chunks.Add(chunk);

            index += (chunkSize - overlap);
            if (index >= text.Length - overlap) break;
        }

        return chunks;
    }

    private async Task<float[]?> GenerateEmbeddingAsync(string text)
    {
        try
        {
            // Resolve the active default model backend
            var defaultModel = "default";
            var (backend, modelName) = await _backends.Resolve(defaultModel);

            if (backend is OllamaBackend)
            {
                // Retrieve base URL using reflection or standard configuration
                // But wait, we can fetch the active backend URL from the database or configuration.
                // An easier way: since we know local llama.cpp is at http://127.0.0.1:11436,
                // and Ollama is typically at http://127.0.0.1:11434, let's query the active backend.
                var enabledBackends = await _db.GetEnabledBackends();
                if (enabledBackends.Count == 0) return null;

                var active = enabledBackends[0];
                var baseUrl = active.BaseUrl.TrimEnd('/');

                if (active.Type == "openai" || active.Type == "openai_compat")
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/embeddings");
                    var payload = new { input = text, model = modelName };
                    req.Content = JsonContent.Create(payload);
                    if (!string.IsNullOrEmpty(active.ApiKey))
                    {
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", active.ApiKey);
                    }
                    var resp = await Http.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) return null;
                    var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                    var data = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
                    return data.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                }
                else if (active.Type == "gemini")
                {
                    var url = $"{baseUrl}/v1beta/models/text-embedding-004:embedContent?key={active.ApiKey}";
                    var payload = new { content = new { parts = new[] { new { text } } } };
                    var resp = await Http.PostAsJsonAsync(url, payload);
                    if (!resp.IsSuccessStatusCode) return null;
                    var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                    var values = doc.RootElement.GetProperty("embedding").GetProperty("values");
                    return values.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                }
                else // Ollama or Llama.cpp
                {
                    // Llama.cpp embedding endpoint: /embedding
                    // Ollama embedding endpoint: /api/embeddings
                    if (active.Type == "ollama" || active.BaseUrl.Contains("11434"))
                    {
                        var resp = await Http.PostAsJsonAsync($"{baseUrl}/api/embeddings", new { model = modelName, prompt = text });
                        if (!resp.IsSuccessStatusCode) return null;
                        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                        var embedding = doc.RootElement.GetProperty("embedding");
                        return embedding.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    }
                    else
                    {
                        // Llama-server embedding endpoint
                        var resp = await Http.PostAsJsonAsync($"{baseUrl}/embedding", new { content = text });
                        if (!resp.IsSuccessStatusCode) return null;
                        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                        var embedding = doc.RootElement.GetProperty("embedding");
                        return embedding.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rag] Embedding generation failed: {ex.Message}");
        }

        return null;
    }

    private static double CosineSimilarity(float[] vectorA, float[] vectorB)
    {
        if (vectorA.Length != vectorB.Length) return 0;

        double dotProduct = 0;
        double normA = 0;
        double normB = 0;

        for (int i = 0; i < vectorA.Length; i++)
        {
            dotProduct += vectorA[i] * vectorB[i];
            normA += vectorA[i] * vectorA[i];
            normB += vectorB[i] * vectorB[i];
        }

        if (normA == 0 || normB == 0) return 0;
        return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    // ── Database Operations ──────────────────────────────────────────────────

    private async Task SaveChunkAsync(string documentId, string filename, string content, float[] embedding)
    {
        // Convert float[] to byte[] for blob storage
        var byteBuffer = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, byteBuffer, 0, byteBuffer.Length);

        await Task.Run(() =>
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO document_chunks (document_id, filename, content, embedding)
                VALUES ($d, $f, $c, $e)
                """;
            cmd.Parameters.AddWithValue("$d", documentId);
            cmd.Parameters.AddWithValue("$f", filename);
            cmd.Parameters.AddWithValue("$c", content);
            cmd.Parameters.AddWithValue("$e", byteBuffer);
            cmd.ExecuteNonQuery();
        });
    }

    private async Task<List<DocumentChunk>> GetChunksAsync(string? filename = null)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            
            if (string.IsNullOrEmpty(filename))
            {
                cmd.CommandText = "SELECT id, document_id, filename, content, embedding FROM document_chunks";
            }
            else
            {
                cmd.CommandText = "SELECT id, document_id, filename, content, embedding FROM document_chunks WHERE filename = $f";
                cmd.Parameters.AddWithValue("$f", filename);
            }

            using var r = cmd.ExecuteReader();
            var list = new List<DocumentChunk>();
            while (r.Read())
            {
                var chunk = new DocumentChunk
                {
                    Id = r.GetInt32(0),
                    DocumentId = r.GetString(1),
                    Filename = r.GetString(2),
                    Content = r.GetString(3)
                };

                // Read BLOB embedding back into float[]
                if (!r.IsDBNull(4))
                {
                    var bytes = (byte[])r.GetValue(4);
                    var floats = new float[bytes.Length / sizeof(float)];
                    Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
                    chunk.Embedding = floats;
                }

                list.Add(chunk);
            }
            return list;
        });
    }
}
