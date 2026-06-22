using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace EmbyAutoTagger;

public class Program
{
    private readonly static HttpClient client = new HttpClient();

    async static Task Main(string[] args)
    {
        // Konfiguration aus Umgebungsvariablen laden
        string embyUrl = Environment.GetEnvironmentVariable("EMBY_URL")?.TrimEnd('/') ?? "http://localhost:8096";
        string apiKey = Environment.GetEnvironmentVariable("EMBY_API_KEY") ?? "";
        string intervalEnv = Environment.GetEnvironmentVariable("SYNC_INTERVAL_MINUTES") ?? "60";

        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("[ERROR] EMBY_API_KEY wurde nicht gesetzt! Das Programm wird beendet.");

            return;
        }

        if (!int.TryParse(intervalEnv, out int intervalMinutes) || intervalMinutes <= 0)
        {
            intervalMinutes = 60;
        }

        Console.WriteLine($"[INFO] Emby Auto-Tagger gestartet.");
        Console.WriteLine($"[INFO] Server: {embyUrl}");
        Console.WriteLine($"[INFO] Intervall: Alle {intervalMinutes} Minuten");

        while (true)
        {
            try
            {
                Console.WriteLine($"[INFO] ({DateTime.Now}) Starte Synchronisation...");
                await SyncEmbyTags(embyUrl, apiKey);
                Console.WriteLine($"[INFO] ({DateTime.Now}) Synchronisation erfolgreich abgeschlossen.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Fehler während der Synchronisation: {ex.Message}");
            }

            // Warten bis zum nächsten Durchlauf
            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes));
        }
    }

    private async static Task SyncEmbyTags(string embyUrl, string apiKey)
    {
        // 1. Alle Filme abrufen, inklusive OfficialRating und Tags
        // Wir filtern nach 'Movie' und nutzen 'Recursive=true' um die ganze Mediathek zu durchsuchen
        string url = $"{embyUrl}/emby/Items?api_key={apiKey}&IncludeItemTypes=Movie&Recursive=true&Fields=OfficialRating,Tags";

        var response = await client.GetFromJsonAsync<EmbyQueryResponse>(url);
        if (response?.Items == null || response.Items.Count == 0)
        {
            Console.WriteLine("[INFO] Keine Filme in Emby gefunden.");

            return;
        }

        foreach (var item in response.Items)
        {
            // Wenn kein Rating vorhanden ist, überspringen
            if (string.IsNullOrWhiteSpace(item.OfficialRating))
                continue;

            // Mapping der Altersfreigabe zum gewünschten Schweizer Intro-Tag
            // Schweizer Kinos nutzen meist rein numerische Werte (6, 8, 12, 14, 16, 18)
            // Hier kannst du das Mapping anpassen, falls Emby z.B. "DE-12" oder "FSK-12" liefert.
            string cleanRating = ExtractNumericRating(item.OfficialRating);

            if (string.IsNullOrEmpty(cleanRating))
                continue;

            string targetTag = $"CH-{cleanRating}";

            // Initialisiere die Tag-Liste, falls sie null ist
            item.Tags ??= new List<string>();

            // Prüfen, ob das Tag bereits existiert
            if (!item.Tags.Contains(targetTag, StringComparer.OrdinalIgnoreCase))
            {
                item.Tags.Add(targetTag);

                // Update an Emby senden
                bool success = await UpdateItemTags(embyUrl, apiKey, item);
                if (success)
                {
                    Console.WriteLine($"[ADDED] Tag '{targetTag}' zu Film hinzugefügt: {item.Name} (Rating war: {item.OfficialRating})");
                }
                else
                {
                    Console.WriteLine($"[FAILED] Konnte Tag für '{item.Name}' nicht aktualisieren.");
                }
            }
        }
    }

    private async static Task<bool> UpdateItemTags(string embyUrl, string apiKey, EmbyItem item)
    {
        // Emby verlangt für Updates ein POST auf das spezifische Item
        string updateUrl = $"{embyUrl}/emby/Items/{item.Id}?api_key={apiKey}";

        // Wir senden das modifizierte Item-Objekt zurück
        var response = await client.PostAsJsonAsync(updateUrl, item);

        return response.IsSuccessStatusCode;
    }

    // Hilfsfunktion, um aus "FSK 12", "DE-12" oder "12" nur die Zahl zu extrahieren
    private static string ExtractNumericRating(string officialRating)
    {
        var digits = officialRating.Where(char.IsDigit).ToArray();

        return digits.Length > 0 ? new string(digits) : string.Empty;
    }
}

// Datenstrukturen für die Emby-API
public class EmbyQueryResponse
{
    [JsonPropertyName("Items")]
    public List<EmbyItem> Items { get; set; } = new();
}

public class EmbyItem
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("OfficialRating")]
    public string? OfficialRating { get; set; }

    [JsonPropertyName("Tags")]
    public List<string>? Tags { get; set; } = new();
}