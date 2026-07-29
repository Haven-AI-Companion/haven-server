using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using AshServer.AI;
using AshServer.Models;

namespace AshServer.Service
{
    public class GroupVoiceCompanionInfo
    {
        public string Name { get; set; } = "Companion";
        public string VoiceId { get; set; } = "en_US-amy-medium";
        public string SystemPrompt { get; set; } = "";
    }

    public class GroupVoiceCallHandler
    {
        public static async Task Handle(
            HttpContext ctx,
            string groupId,
            BackendManager backends,
            IConfiguration config)
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();

            // Handshake first message
            var firstMsgBuf = new byte[8192];
            using var firstMsgMs = new MemoryStream();
            WebSocketReceiveResult firstMsgResult;
            do
            {
                firstMsgResult = await ws.ReceiveAsync(firstMsgBuf, CancellationToken.None);
                if (firstMsgResult.MessageType == WebSocketMessageType.Close) return;
                firstMsgMs.Write(firstMsgBuf, 0, firstMsgResult.Count);
            } while (!firstMsgResult.EndOfMessage);

            var characters = new List<GroupVoiceCompanionInfo>();
            string groupName = "Group Call";

            try
            {
                using var doc = JsonDocument.Parse(firstMsgMs.ToArray());
                if (doc.RootElement.TryGetProperty("groupName", out var gn)) groupName = gn.GetString() ?? groupName;
                if (doc.RootElement.TryGetProperty("characters", out var charsElem) && charsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in charsElem.EnumerateArray())
                    {
                        var c = new GroupVoiceCompanionInfo();
                        if (elem.TryGetProperty("name", out var n)) c.Name = n.GetString() ?? c.Name;
                        if (elem.TryGetProperty("voiceId", out var v)) c.VoiceId = v.GetString() ?? c.VoiceId;
                        if (elem.TryGetProperty("systemPrompt", out var sp)) c.SystemPrompt = sp.GetString() ?? c.SystemPrompt;
                        characters.Add(c);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GroupVoice] Handshake parse error: {ex.Message}");
            }

            if (characters.Count == 0)
            {
                characters.Add(new GroupVoiceCompanionInfo { Name = "Wanda", VoiceId = "bf_emma" });
                characters.Add(new GroupVoiceCompanionInfo { Name = "Mika", VoiceId = "af_bella" });
            }

            Console.WriteLine($"[GroupVoice] Started multi-companion group call '{groupName}' with {characters.Count} companions ({string.Join(", ", characters.Select(c => c.Name))})");

            var modelId = config["DefaultModel"] ?? "default";
            var piperExe = @"C:\Users\admin\piper\piper\piper.exe";

            int speakerIndex = 0;

            while (ws.State == WebSocketState.Open)
            {
                using var textMs = new MemoryStream();
                var recvBuf = new byte[8192];
                WebSocketReceiveResult recvResult;

                do
                {
                    recvResult = await ws.ReceiveAsync(recvBuf, CancellationToken.None);
                    if (recvResult.MessageType == WebSocketMessageType.Close) return;
                    if (recvResult.MessageType == WebSocketMessageType.Text)
                    {
                        textMs.Write(recvBuf, 0, recvResult.Count);
                    }
                } while (!recvResult.EndOfMessage);

                string promptText = Encoding.UTF8.GetString(textMs.ToArray()).Trim();
                if (string.IsNullOrEmpty(promptText)) continue;

                // Pick active companion for this turn
                var currentComp = characters[speakerIndex % characters.Count];
                speakerIndex++;

                Console.WriteLine($"[GroupVoice] User prompt: '{promptText}' -> Routing response to {currentComp.Name}");

                // System prompt for character
                var sysPrompt = $"You are {currentComp.Name} in a live multi-person group voice call with the user and your friends ({string.Join(", ", characters.Select(c => c.Name))}). Speak naturally, keep your response short (1-2 sentences), playful, and in character. {currentComp.SystemPrompt}";

                var messages = new List<ChatMessage>
                {
                    new ChatMessage("system", sysPrompt),
                    new ChatMessage("user", promptText)
                };

                var responseTextBuilder = new StringBuilder();
                await foreach (var token in backends.StreamChat(modelId, messages, CancellationToken.None))
                {
                    responseTextBuilder.Append(token);
                }

                var fullResponse = responseTextBuilder.ToString().Trim();
                var cleanText = System.Text.RegularExpressions.Regex.Replace(fullResponse, @"<thought>.*?</thought>", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();

                if (string.IsNullOrEmpty(cleanText)) cleanText = "I'm right here listening!";

                // Send speech metadata JSON
                var speechMeta = JsonSerializer.Serialize(new
                {
                    type = "speech_start",
                    characterName = currentComp.Name,
                    voiceId = currentComp.VoiceId,
                    text = cleanText
                });
                var metaBytes = Encoding.UTF8.GetBytes(speechMeta);
                await ws.SendAsync(new ArraySegment<byte>(metaBytes), WebSocketMessageType.Text, true, CancellationToken.None);

                // Synthesize TTS audio if Piper model exists
                var piperModel = $@"C:\Users\admin\piper\piper\models\{currentComp.VoiceId}.onnx";
                if (!File.Exists(piperModel)) piperModel = @"C:\Users\admin\piper\piper\models\en_US-amy-medium.onnx";

                if (File.Exists(piperExe) && File.Exists(piperModel))
                {
                    try
                    {
                        var tempWav = Path.Combine(Path.GetTempPath(), $"group_tts_{Guid.NewGuid()}.wav");
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = piperExe,
                            Arguments = $"--model \"{piperModel}\" --output_file \"{tempWav}\"",
                            RedirectStandardInput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var proc = System.Diagnostics.Process.Start(psi);
                        if (proc != null)
                        {
                            await proc.StandardInput.WriteLineAsync(cleanText);
                            await proc.StandardInput.FlushAsync();
                            proc.StandardInput.Close();
                            await proc.WaitForExitAsync();

                            if (File.Exists(tempWav))
                            {
                                var audioData = await File.ReadAllBytesAsync(tempWav);
                                try { File.Delete(tempWav); } catch { }

                                // Send WAV binary audio
                                await ws.SendAsync(new ArraySegment<byte>(audioData), WebSocketMessageType.Binary, true, CancellationToken.None);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GroupVoice] TTS synthesis error: {ex.Message}");
                    }
                }
            }
        }
    }
}
