using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// CORS simple
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
));

// Archivos estáticos (wwwroot)
builder.Services.AddDirectoryBrowser();

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------- Config ----------
var cfg = app.Configuration.GetSection("WhatsApp").Get<WaConfig>()
          ?? throw new Exception("Falta sección WhatsApp en appsettings.json");

var http = new HttpClient();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.AccessToken);

// ---------- Memoria en proceso (demo) ----------
var store = new ConversationStore(); // para prod, usar DB

// ---------- Webhook: Verify (GET) ----------
app.MapGet("/webhook", (HttpRequest req) =>
{
    var mode = req.Query["hub.mode"].ToString();
    var token = req.Query["hub.verify_token"].ToString();
    var challenge = req.Query["hub.challenge"].ToString();

    if (mode == "subscribe" && token == cfg.VerifyToken)
        return Results.Text(challenge, "text/plain", Encoding.UTF8);

    return Results.Unauthorized();
});

// ---------- Webhook: Receive (POST) ----------
app.MapPost("/webhook", async (HttpRequest req) =>
{
    string raw;
    using (var reader = new StreamReader(req.Body, Encoding.UTF8))
        raw = await reader.ReadToEndAsync();

    Console.WriteLine("=== WEBHOOK RAW ===");
    Console.WriteLine(raw);

    try
    {
        using var doc = JsonDocument.Parse(raw);
        var body = doc.RootElement;

        if (body.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes)) continue;

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value)) continue;

                    var messages = value.GetPropertyOrNull("messages");
                    if (messages == null || messages.Value.ValueKind != JsonValueKind.Array) continue;

                    foreach (var msg in messages.Value.EnumerateArray())
                    {
                        var from = msg.GetPropertyOrNull("from")?.GetString();
                        var type = msg.GetPropertyOrNull("type")?.GetString();
                        var msgId = msg.GetPropertyOrNull("id")?.GetString();
                        string? text = null;

                        if (type == "text")
                            text = msg.GetPropertyOrNull("text")?.GetPropertyOrNull("body")?.GetString();
                        else if (type == "button")
                            text = msg.GetPropertyOrNull("button")?.GetPropertyOrNull("text")?.GetString();
                        else if (type == "interactive")
                            text = msg.GetPropertyOrNull("interactive")?.GetPropertyOrNull("button_reply")?.GetPropertyOrNull("title")?.GetString()
                                   ?? msg.GetPropertyOrNull("interactive")?.GetPropertyOrNull("list_reply")?.GetPropertyOrNull("title")?.GetString()
                                   ?? "(interactive)";

                        if (!string.IsNullOrWhiteSpace(from))
                        {
                            store.Append(from!, new ChatMessage
                            {
                                Id = msgId ?? Guid.NewGuid().ToString("N"),
                                WaId = from!,
                                Direction = "in",
                                Body = text ?? $"[{type}]",
                                Ts = DateTimeOffset.UtcNow
                            });
                        }
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WEBHOOK ERR] {ex}");
    }

    // Siempre devolver 200 a Meta
    return Results.Ok();
});


// ---------- API: listar conversaciones (último mensaje por contacto) ----------
app.MapGet("/api/conversations", () =>
{
    var list = store.ListConversations()
                    .Select(x => new { waId = x.WaId, lastBody = x.LastBody, lastTs = x.LastTs })
                    .OrderByDescending(c => c.lastTs)
                    .ToList();
    return Results.Json(list);
});

// ---------- API: mensajes de un contacto ----------
app.MapGet("/api/messages/{waId}", (string waId) =>
{
    var msgs = store.Get(waId);
    return Results.Json(msgs ?? new List<ChatMessage>());
});

// ---------- API: enviar respuesta (texto) ----------
app.MapPost("/api/send", async (SendRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.To) || string.IsNullOrWhiteSpace(req.Text))
        return Results.BadRequest(new { error = "to y text son requeridos" });

    // Payload básico
    var template = new Dictionary<string, object?>
    {
        ["messaging_product"] = "whatsapp",
        ["to"] = req.To,
        ["type"] = "text",
        ["text"] = new { body = req.Text }
    };

    app.MapGet("/webhook", (HttpRequest req) =>
    {
        var mode = req.Query["hub.mode"].ToString();
        var token = req.Query["hub.verify_token"].ToString();
        var challenge = req.Query["hub.challenge"].ToString();

        Console.WriteLine($"[VERIFY] mode={mode}, provided_len={token?.Length}, expected_len={cfg.VerifyToken?.Length}");

        if (mode == "subscribe" && token == cfg.VerifyToken)
            return Results.Text(challenge, "text/plain", Encoding.UTF8);

        return Results.Unauthorized();
    });


    // Si viene ReplyTo (wamid del mensaje citado), agregamos context
    if (!string.IsNullOrWhiteSpace(req.ReplyTo))
    {
        template["context"] = new { message_id = req.ReplyTo };
    }

    var msgReq = new HttpRequestMessage(
        HttpMethod.Post,
        $"https://graph.facebook.com/v20.0/{cfg.PhoneNumberId}/messages"
    );
    msgReq.Content = new StringContent(JsonSerializer.Serialize(template), Encoding.UTF8, "application/json");

    using var resp = await http.SendAsync(msgReq);
    var json = await resp.Content.ReadAsStringAsync();

    if (!resp.IsSuccessStatusCode)
        return Results.Content(json, "application/json", Encoding.UTF8, (int)resp.StatusCode);

    // Guardar como 'out' (id local; si quisieras, podés parsear el wamid del response)
    store.Append(req.To, new ChatMessage
    {
        Id = "out-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        WaId = req.To,
        Direction = "out",
        Body = req.Text,
        Ts = DateTimeOffset.UtcNow
    });

    return Results.Content(json, "application/json", Encoding.UTF8);
});

// ---------- UI ----------
app.MapFallbackToFile("index.html");

app.Run();


// ======== tipos / util ========
record WaConfig
{
    public string AccessToken { get; init; } = "";
    public string PhoneNumberId { get; init; } = "";
    public string VerifyToken { get; init; } = "my_verify_token";
}

class ConversationStore
{
    // wa_id -> lista de mensajes
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _data = new();

    public void Append(string waId, ChatMessage msg)
    {
        var list = _data.GetOrAdd(waId, _ => new List<ChatMessage>());
        lock (list) list.Add(msg);
    }

    public IEnumerable<(string WaId, string LastBody, DateTimeOffset LastTs)> ListConversations()
    {
        foreach (var kv in _data)
        {
            var list = kv.Value;
            ChatMessage last;
            lock (list) last = list.OrderBy(m => m.Ts).LastOrDefault()
                           ?? new ChatMessage { Body = "", Ts = DateTimeOffset.MinValue };
            yield return (kv.Key, last.Body ?? "", last.Ts);
        }
    }

    public List<ChatMessage>? Get(string waId)
        => _data.TryGetValue(waId, out var l) ? l.OrderBy(m => m.Ts).ToList() : null;
}

class ChatMessage
{
    [JsonPropertyName("id")] public string? Id { get; set; }          // wamid
    [JsonPropertyName("wa_id")] public string WaId { get; set; } = "";
    [JsonPropertyName("direction")] public string Direction { get; set; } = "in"; // in | out
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("ts")] public DateTimeOffset Ts { get; set; }
}

record SendRequest(string To, string Text, string? ReplyTo);

static class JsonExt
{
    public static JsonElement? GetPropertyOrNull(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : (JsonElement?)null;
}
