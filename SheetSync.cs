using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SapAssistente;

internal sealed record OrderSheetRow(string Ordem, string DataAbertura, string Frota, string Empresa, string Status, string Tipo, string Nome);

internal static class SheetSync
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(18) };

    public static async Task<bool> PublishPendingAsync(OperationJournal journal, IProgress<string> progress, bool requested = false)
    {
        var pending = journal.PendingSheetRows().ToList();
        if (pending.Count == 0)
        {
            if (requested) progress.Report("Nenhuma ordem pendente para a planilha.");
            return true;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "planilha.local.json");
        if (!File.Exists(path))
        {
            progress.Report($"Planilha: {pending.Count} ordem(ns) aguardando configuração em planilha.local.json.");
            return false;
        }

        var settings = JsonSerializer.Deserialize<SheetSettings>(File.ReadAllText(path), Program.JsonOptions);
        if (settings?.Chave is not { Length: >= 32 and <= 256 } secret || string.IsNullOrWhiteSpace(secret) || !DeploymentUrl(settings.Url, out var url))
            throw new InvalidDataException("Configure a URL /exec e a chave em planilha.local.json.");

        foreach (var (recordPath, row) in pending)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var duplicate = await SendAsync(Client, url, secret, row, timeout.Token).ConfigureAwait(false);
                OperationJournal.MarkSheetSent(recordPath);
                progress.Report(duplicate ? $"Planilha: ordem {row.Ordem} já registrada." : $"Planilha: ordem {row.Ordem} registrada.");
            }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException)
            {
                progress.Report($"Planilha: ordem {row.Ordem} pendente. {PublicError(error)} Use Sincronizar planilha para tentar de novo.");
                return false;
            }
        }
        return true;
    }

    private static string PublicError(Exception error) => error switch
    {
        InvalidOperationException => error.Message,
        OperationCanceledException => "O Google demorou para responder.",
        IOException or UnauthorizedAccessException => "Não foi possível atualizar o histórico local.",
        _ => "Não foi possível confirmar o envio ao Google."
    };

    private static bool DeploymentUrl(string? raw, out Uri url)
    {
        url = null!;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed) || !OnGoogle(parsed, "script.google.com") || parsed.Query.Length != 0 || parsed.Fragment.Length != 0 ||
            !Regex.IsMatch(parsed.AbsolutePath, @"\A/(?:a/)?macros/(?:[A-Za-z0-9.-]+/)?s/[A-Za-z0-9_-]+/exec\z", RegexOptions.CultureInvariant))
            return false;
        url = parsed;
        return true;
    }

    private static bool OnGoogle(Uri url, string host) => url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length == 0 && url.Host == host;

    private static async Task<bool> SendAsync(HttpClient http, Uri url, string secret, OrderSheetRow row, CancellationToken cancel)
    {
        var payload = new { chave = secret, row.Ordem, row.DataAbertura, row.Frota, row.Empresa, row.Status, row.Tipo, row.Nome };
        using var post = await http.PostAsJsonAsync(url, payload, cancel).ConfigureAwait(false);
        if (post.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod)
        {
            var location = post.Headers.Location;
            if (location is null || !location.IsAbsoluteUri || !OnGoogle(location, "script.googleusercontent.com"))
                throw new InvalidOperationException("O Google redirecionou para uma página de acesso. Verifique a implantação.");
            using var redirected = await http.GetAsync(location, cancel).ConfigureAwait(false);
            return await CheckReply(redirected, row.Ordem, cancel).ConfigureAwait(false);
        }
        return await CheckReply(post, row.Ordem, cancel).ConfigureAwait(false);
    }

    private static async Task<bool> CheckReply(HttpResponseMessage response, string order, CancellationToken cancel)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidOperationException("O Google não confirmou o envio. Verifique o acesso à implantação.");
        var reply = await response.Content.ReadFromJsonAsync<SheetReply>(cancellationToken: cancel).ConfigureAwait(false);
        if (reply is not { Ok: true } || reply.Ordem != order)
            throw new InvalidOperationException("O script da planilha recusou a ordem. Verifique a chave, as propriedades e a aba configurada.");
        return reply.Duplicado;
    }

    private sealed record SheetSettings(string? Url, string? Chave);
    private sealed record SheetReply(bool Ok, string? Ordem, bool Duplicado);

    internal static void RunSelfTests()
    {
        if (!DeploymentUrl("https://script.google.com/macros/s/TESTE/exec", out var target) ||
            DeploymentUrl("https://script.google.com.example.org/macros/s/TESTE/exec", out _))
            throw new InvalidOperationException("Validação do endereço da planilha falhou.");
        var calls = 0;
        using var http = new HttpClient(new RecordingHandler(async request =>
        {
            if (++calls == 1)
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                if (request.Method != HttpMethod.Post || payload.RootElement.GetProperty("ordem").GetString() != "000123" ||
                    payload.RootElement.GetProperty("dataAbertura").GetString() != "2026-01-02" || payload.RootElement.GetProperty("chave").GetString() != "segredo-de-teste")
                    throw new InvalidOperationException("Conteúdo do envio incorreto.");
                return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://script.googleusercontent.com/macros/echo") } };
            }
            if (request.Method != HttpMethod.Get || request.Content is not null)
                throw new InvalidOperationException("O redirecionamento não deve reenviar a chave.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true, ordem = "000123", duplicado = true }) };
        }));
        var row = new OrderSheetRow("000123", "2026-01-02", "0001", "Exemplo", "Aberta", "Serviço", "Teste");
        if (!SendAsync(http, target, "segredo-de-teste", row, CancellationToken.None).GetAwaiter().GetResult() || calls != 2)
            throw new InvalidOperationException("A confirmação idempotente da planilha falhou.");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
