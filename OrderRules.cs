using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SapAssistente;

public enum OrderRoute
{
    Manual,
    ServiceOrder,
    PartRequest,
    ServiceOrderAndRequest
}

public sealed record OrderSuggestion(
    OrderRoute Route,
    string? Damage,
    string? CollectionType,
    string? NoteCodeGroup,
    string? NoteCode,
    string Reason);

public sealed record OrderTitleRule(
    string MatchPhrase,
    OrderRoute Route,
    int Priority = 0,
    string? Damage = null,
    string? CollectionType = null,
    string? NoteCodeGroup = null,
    string? NoteCode = null);

public static class OrderRules
{
    private static readonly string[] PartPhrases =
    [
        "pedido de peca", "pedido de pecas", "requisicao de peca", "requisicao de pecas",
        "requisicao de material", "requisicao de materiais", "pedido de material",
        "pedido de materiais", "compra de peca", "compra de pecas", "compra de material",
        "compra de materiais"
    ];

    private static readonly string[] ServiceOnlyPhrases =
    [
        "somente ordem", "apenas ordem", "somente servico", "apenas servico",
        "somente mao de obra", "apenas mao de obra"
    ];

    private static readonly string[] ServiceRequestPhrases =
    [
        "requisicao de servico", "requisicao de servicos", "compra de servico",
        "compra de servicos", "pedido de servico", "pedido de servicos"
    ];

    private static readonly string[] NegationWords = ["nao", "sem", "dispensa", "dispensar"];

    public static OrderSuggestion Suggest(string? title, IEnumerable<OrderTitleRule>? profileRules = null)
    {
        var normalized = Normalize(title);
        if (normalized.Length == 0)
            return Manual("Digite o nome da ordem.");

        var paddedTitle = $" {normalized} ";
        var hasPartRequest = MatchesAny(paddedTitle, PartPhrases);
        var hasServiceOnly = MatchesAny(paddedTitle, ServiceOnlyPhrases);
        var hasServiceRequest = MatchesAny(paddedTitle, ServiceRequestPhrases);
        var hasServiceOrder = ContainsPhrase(paddedTitle, "ordem de servico");
        var hasTireService = ContainsPhrase(paddedTitle, "borracharia");

        if (MatchesAny(paddedTitle, NegationWords))
            return Manual("O nome contém uma ressalva. Selecione o fluxo após conferir a descrição.");

        if ((hasPartRequest && (hasServiceOnly || hasServiceOrder || hasServiceRequest || hasTireService))
            || (hasServiceOnly && (hasServiceRequest || hasTireService)))
        {
            return Manual("O nome indica mais de um fluxo. Selecione o que deve ser aberto.");
        }

        var matchingRules = profileRules?
            .Where(rule => rule is not null && ContainsPhrase(paddedTitle, Normalize(rule.MatchPhrase)))
            .ToArray() ?? [];

        if (matchingRules.Length > 0)
        {
            var highestPriority = matchingRules.Max(rule => rule.Priority);
            var suggestions = matchingRules
                .Where(rule => rule.Priority == highestPriority)
                .Select(ProfileSuggestion)
                .Distinct()
                .ToArray();
            if (suggestions.Length > 1)
                return Manual("As regras configuradas de mesma prioridade sugerem resultados diferentes.");

            var suggestion = suggestions[0];
            if (hasPartRequest && suggestion.Route != OrderRoute.PartRequest
                || hasServiceOnly && suggestion.Route != OrderRoute.ServiceOrder
                || hasServiceRequest && suggestion.Route != OrderRoute.ServiceOrderAndRequest)
            {
                return Manual("A regra configurada diverge da descrição. Selecione o fluxo após conferir.");
            }

            return suggestion;
        }

        if (hasTireService)
        {
            return new(OrderRoute.ServiceOrderAndRequest, "Borracharia", null, null, null,
                "Borracharia sugere uma ordem vinculada a uma requisição de serviço.");
        }

        if (hasPartRequest)
            return new(OrderRoute.PartRequest, null, null, null, null, "O nome indica pedido de peça.");

        if (hasServiceOrder && hasServiceRequest)
        {
            return new(OrderRoute.ServiceOrderAndRequest, null, null, null, null,
                "O nome indica ordem e requisição de serviço.");
        }

        if (hasServiceOnly || hasServiceOrder)
            return new(OrderRoute.ServiceOrder, null, null, null, null, "O nome indica ordem de serviço.");

        return Manual("Defeito ou peça isolada pode exigir serviço ou compra. Selecione o fluxo após conferir.");
    }

    public static string DisplayName(OrderRoute route) => route switch
    {
        OrderRoute.ServiceOrder => "Somente ordem de serviço",
        OrderRoute.PartRequest => "Pedido de peça",
        OrderRoute.ServiceOrderAndRequest => "Ordem + requisição de serviço",
        _ => "Selecione o fluxo"
    };

    private static OrderSuggestion ProfileSuggestion(OrderTitleRule rule)
    {
        if (!Enum.IsDefined(rule.Route) || rule.Route == OrderRoute.Manual)
            return Manual("A regra configurada pede uma seleção manual do fluxo.");

        return new(rule.Route, TrimOrNull(rule.Damage), TrimOrNull(rule.CollectionType),
            TrimOrNull(rule.NoteCodeGroup), TrimOrNull(rule.NoteCode), "Sugestão da regra configurada.");
    }

    private static OrderSuggestion Manual(string reason) => new(OrderRoute.Manual, null, null, null, null, reason);

    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool MatchesAny(string paddedTitle, string[] phrases) =>
        phrases.Any(phrase => ContainsPhrase(paddedTitle, phrase));

    private static bool ContainsPhrase(string paddedTitle, string phrase) =>
        phrase.Length > 0 && paddedTitle.Contains($" {phrase} ", StringComparison.Ordinal);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = new StringBuilder(value.Length);
        var needsSpace = false;
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (!char.IsLetterOrDigit(character))
            {
                needsSpace = result.Length > 0;
                continue;
            }

            if (needsSpace)
                result.Append(' ');

            result.Append(char.ToLowerInvariant(character));
            needsSpace = false;
        }

        return result.ToString();
    }

    public static void RunSelfTests()
    {
        AssertRoute(null, OrderRoute.Manual);
        AssertRoute("freio", OrderRoute.Manual);
        AssertRoute("reborracharia", OrderRoute.Manual);
        AssertRoute("SERV. BORRACHARIA", OrderRoute.ServiceOrderAndRequest);
        AssertRoute("REQUISIÇÃO-DE-PEÇAS", OrderRoute.PartRequest);
        AssertRoute("Ordem de serviço", OrderRoute.ServiceOrder);
        AssertRoute("Ordem de serviço e pedido de peça", OrderRoute.Manual);
        AssertRoute("Ordem de serviço e compra de material", OrderRoute.Manual);
        AssertRoute("Ordem de serviço e requisição de serviço", OrderRoute.ServiceOrderAndRequest);
        AssertRoute("Requisição de serviço", OrderRoute.Manual);
        AssertRoute("Somente ordem de borracharia", OrderRoute.Manual);
        AssertRoute("Sem pedido de peça", OrderRoute.Manual);
        AssertRoute("Borracharia sem conserto", OrderRoute.Manual);

        OrderTitleRule[] rules =
        [
            new("alinhamento", OrderRoute.ServiceOrder, 10, "Desalinhado"),
            new("alinhamento dianteiro", OrderRoute.ServiceOrderAndRequest, 20,
                "Frente desalinhada", "Oficina", "COD01", "A1")
        ];
        var suggestion = Suggest("ALINHAMENTO  DIANTEIRO", rules);
        if (suggestion is not { Route: OrderRoute.ServiceOrderAndRequest, Damage: "Frente desalinhada",
                CollectionType: "Oficina", NoteCodeGroup: "COD01", NoteCode: "A1" })
        {
            throw new InvalidOperationException("Sugestão configurada com prioridade superior não foi aplicada.");
        }

        AssertRoute("alinhamento dianteiro", OrderRoute.Manual,
            [.. rules, new("dianteiro", OrderRoute.ServiceOrder, 20)]);
        AssertRoute("realinhamento", OrderRoute.Manual, rules);
        AssertRoute("pedido de peça para alinhamento", OrderRoute.Manual, rules);

        if (Suggest("Borracharia").CollectionType is not null)
            throw new InvalidOperationException("Tipo de recolhimento ausente precisa ficar em aberto.");
    }

    private static void AssertRoute(string? title, OrderRoute expected, IEnumerable<OrderTitleRule>? rules = null)
    {
        var actual = Suggest(title, rules).Route;
        if (actual != expected)
            throw new InvalidOperationException($"Fluxo inesperado para '{title}': {actual}. Esperado: {expected}.");
    }
}
