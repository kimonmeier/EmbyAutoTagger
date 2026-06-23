using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Emby.ApiClient.Api;
using Emby.ApiClient.Client;
using Emby.ApiClient.Client.Authentication;
using Emby.ApiClient.Model;
using RestSharp;

namespace EmbyAutoTagger;

public class Program
{
    async static Task Main(string[] args)
    {
        // Konfiguration aus Umgebungsvariablen laden
        string embyUrl = Environment.GetEnvironmentVariable("EMBY_URL")?.TrimEnd('/') ?? "http://localhost:8096";
        string apiKey = Environment.GetEnvironmentVariable("EMBY_API_KEY") ?? "";
        string intervalEnv = Environment.GetEnvironmentVariable("SYNC_INTERVAL_MINUTES") ?? "60";
        string tags = Environment.GetEnvironmentVariable("TAGS") ?? "";

        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("[ERROR] EMBY_API_KEY wurde nicht gesetzt! Das Programm wird beendet.");

            return;
        }

        if (string.IsNullOrEmpty(tags))
        {
            Console.WriteLine("[ERROR] TAGS wurde nicht gesetzt! Das Programm wird beendet.");

            return;
        }
        
        if (!int.TryParse(intervalEnv, out int intervalMinutes) || intervalMinutes <= 0)
        {
            intervalMinutes = 60;
        }
        
        var tagList = JsonSerializer.Deserialize<NameLongIdPair[]>(tags);

        if (tagList == null || !tagList.Any())
        {
            Console.WriteLine("[ERROR] TAGS konnte nicht deserialisiert werden! Das Programm wird beendet.");
            return;
        }

        Console.WriteLine($"[INFO] Emby Auto-Tagger gestartet.");
        Console.WriteLine($"[INFO] Server: {embyUrl}");
        Console.WriteLine($"[INFO] Intervall: Alle {intervalMinutes} Minuten");

        while (true)
        {
            try
            {
                Console.WriteLine($"[INFO] ({DateTime.Now}) Starte Synchronisation...");
                await SyncEmbyTags(embyUrl, apiKey, tagList);
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

    private async static Task SyncEmbyTags(string embyUrl, string apiKey, NameLongIdPair[] tagList)
    {

        ApiClient apiClient = new ApiClient(embyUrl, new EmbyApiKeyAuthenticator(apiKey));

        const int pageSize = 500;
        int startIndex = 0;
        int total;

        do
        {
            var url =
                $"emby/Items?Recursive=true" +
                $"&IncludeItemTypes=Movie" +
                $"&Fields=Tags,OfficialRating" +
                $"&StartIndex={startIndex}" +
                $"&Limit={pageSize}";

            var page = await apiClient.RestClient.ExecuteGetAsync<QueryResultBaseItemDto>(url);

            if (page.Data == null || page.Data.TotalRecordCount == 0)
            {
                if (startIndex == 0)
                    Console.WriteLine("[INFO] Keine Filme in Emby gefunden.");
                return;
            }

            total = page.Data.TotalRecordCount ?? 0;

            foreach (var item in page.Data.Items)
            {
                if (string.IsNullOrWhiteSpace(item.OfficialRating))
                    continue;

                string cleanRating = ExtractNumericRating(item.OfficialRating);
                if (string.IsNullOrEmpty(cleanRating))
                    continue;

                
                
                string targetTag = $"CH-{cleanRating}";

                item.TagItems ??= new List<NameLongIdPair>();

                if (!item.TagItems.Any(x => x.Name.Equals(targetTag, StringComparison.OrdinalIgnoreCase)))
                {
                    var tag = tagList.FirstOrDefault(x => x.Name.Equals(targetTag, StringComparison.OrdinalIgnoreCase));

                    if (tag == null)
                    {
                        // Find next higher rating
                        if (int.TryParse(cleanRating, out int currentRating))
                        {
                            var availableRatings = tagList
                                .Select(t => new { Tag = t, Rating = ExtractNumericRating(t.Name ?? "") })
                                .Where(x => !string.IsNullOrEmpty(x.Rating) && int.TryParse(x.Rating, out _))
                                .Select(x => new { x.Tag, RatingValue = int.Parse(x.Rating) })
                                .OrderBy(x => x.RatingValue)
                                .ToList();

                            tag = availableRatings.FirstOrDefault(x => x.RatingValue >= currentRating)?.Tag;
                        }
                    }

                    if (tag != null)
                    {
                        // Remove all existing CH-* tags that don't match the target
                        var existingChTags = item.TagItems
                            .Where(t => t.Name != null && t.Name.StartsWith("CH-", StringComparison.OrdinalIgnoreCase))
                            .Where(t => !t.Name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var existingTag in existingChTags)
                        {
                            await RemoveItemTag(item.Id, existingTag.Name, existingTag.Id, apiClient);
                            Console.WriteLine($"[REMOVED] Tag '{existingTag.Name}' von Film entfernt: {item.Name}");
                        }

                        bool success = await AddItemTag(item.Id, tag.Name, tag.Id, apiClient);
                        if (success)
                        {
                            string usedTag = tag.Name ?? "";
                            if (usedTag.Equals(targetTag, StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"[ADDED] Tag '{targetTag}' zu Film hinzugefügt: {item.Name} (Rating war: {item.OfficialRating})");
                            }
                            else
                            {
                                Console.WriteLine($"[ADDED] Tag '{usedTag}' (nächsthöheres Rating) zu Film hinzugefügt: {item.Name} (Rating war: {item.OfficialRating}, Ziel war: {targetTag})");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[FAILED] Konnte Tag für '{item.Name}' nicht aktualisieren.");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[SKIPPED] Kein passendes Tag gefunden für '{item.Name}' mit Rating {item.OfficialRating}");
                    }
                }
            }

            startIndex += pageSize;
        } while (startIndex < total);
    }

    private async static Task<bool> AddItemTag(string movieId, string rating, long? ratingId, ApiClient client)
    {
        var url = $"Items/{movieId}/Tags/Add";

        object request = new
        {
            tags = new dynamic[]
            {
                new
                {
                    name = rating,
                    id = ratingId,
                }
            }
        };

        var response =  await client.RestClient.PostJsonAsync(url, request);

        if (response != HttpStatusCode.NoContent)
        {
            Console.WriteLine($"[ERROR] Fehler beim Aktualisieren des Items: {response}");

            return false;
        }

        return true;
    }

    private async static Task<bool> RemoveItemTag(string movieId, string rating, long? ratingId, ApiClient client)
    {
        var url = $"Items/{movieId}/Tags/Delete";

        object request = new
        {
            tags = new dynamic[]
            {
                new
                {
                    name = rating,
                    id = ratingId
                }
            }
        };

        var response =  await client.RestClient.PostJsonAsync(url, request);

        if (response != HttpStatusCode.NoContent)
        {
            Console.WriteLine($"[ERROR] Fehler beim Entfernen des Tags: {response}");
            
            return false;
        }

        return true;
    }

    // Hilfsfunktion, um aus "FSK 12", "DE-12" oder "12" nur die Zahl zu extrahieren
    private static string ExtractNumericRating(string officialRating)
    {
        var digits = officialRating.Where(char.IsDigit).ToArray();

        return digits.Length > 0 ? new string(digits) : string.Empty;
    }
}