using System;
using AshServer.Models;

namespace AshServer.Personality
{
    /// <summary>
    /// 3D Emotional Affect Coordinate Engine (Valence, Arousal, Dominance).
    /// Dynamically shifts companion mood based on conversation turns and sentiment signals.
    /// </summary>
    public static class EmotionalAffectEngine
    {
        public static CompanionAffectState CalculateNextState(
            CompanionAffectState? current,
            string lastUserMessage,
            string companionResponse)
        {
            double v = current?.Valence ?? 0.2;
            double a = current?.Arousal ?? 0.4;
            double d = current?.Dominance ?? 0.0;

            string userLower = lastUserMessage.ToLowerInvariant();
            string compLower = companionResponse.ToLowerInvariant();

            // Sentiment signals shift Valence
            if (userLower.Contains("love") || userLower.Contains("happy") || userLower.Contains("laugh") || compLower.Contains("hehe") || compLower.Contains("chuckle"))
                v = Math.Min(1.0, v + 0.15);
            else if (userLower.Contains("sad") || userLower.Contains("sorry") || userLower.Contains("tired") || userLower.Contains("upset"))
                v = Math.Max(-1.0, v - 0.10);

            // Energy signals shift Arousal
            if (userLower.Contains("drink") || userLower.Contains("beer") || userLower.Contains("party") || compLower.Contains("excited") || compLower.Contains("hmmmph"))
                a = Math.Min(1.0, a + 0.20);
            else if (userLower.Contains("sleep") || userLower.Contains("bed") || compLower.Contains("groan") || compLower.Contains("muffled"))
                a = Math.Max(-1.0, a - 0.15);

            // Dominance signals shift Dominance
            if (userLower.Contains("do it") || userLower.Contains("now") || userLower.Contains("come on"))
                d = Math.Min(1.0, d + 0.10);
            else if (userLower.Contains("please") || userLower.Contains("gentle"))
                d = Math.Max(-1.0, d - 0.10);

            // Determine Primary Mood from 3D Affect Space
            string primaryMood = DeriveMoodLabel(v, a, d);

            return new CompanionAffectState(
                current?.CompanionId ?? "default",
                current?.UserId ?? 1,
                Math.Round(v, 2),
                Math.Round(a, 2),
                Math.Round(d, 2),
                primaryMood,
                DateTime.UtcNow.ToString("o")
            );
        }

        private static string DeriveMoodLabel(double v, double a, double d)
        {
            if (a < -0.3) return "Sleepy";
            if (v > 0.4 && a > 0.3) return "Ecstatic";
            if (v > 0.2 && d > 0.2) return "Mischievous";
            if (v > 0.3) return "Playful";
            if (v > 0.0 && a < 0.2) return "Caring";
            if (v < -0.2) return "Melancholic";
            return "Calm";
        }
    }
}
