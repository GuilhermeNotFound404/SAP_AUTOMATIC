using System.Text.Json;
using System.Text.Json.Serialization;

namespace SapAssistente;

internal static class Program
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    [STAThread]
    private static int Main(string[] args)
    {
        switch (args)
        {
            case ["--testar", var resultPath]:
                OrderRules.RunSelfTests();
                OperationJournal.RunSelfTests();
                File.WriteAllText(resultPath, "OK");
                return 0;
            case ["--verificar-interface", var markerPath]:
                try
                {
                    ApplicationConfiguration.Initialize();
                    using var form = new MainForm(new LocalSettings(), null);
                    form.TestClassification();
                    form.Shown += (_, _) => form.BeginInvoke(form.Close);
                    Application.Run(form);
                    File.WriteAllText(markerPath, "OK");
                }
                catch (Exception error)
                {
                    File.WriteAllText(markerPath, error.ToString());
                    return 1;
                }
                return 0;
        }

        ApplicationConfiguration.Initialize();
        var settings = new LocalSettings();
        string? settingsError = null;
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "regras.local.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                settings = JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(settingsPath), JsonOptions)
                    ?? throw new InvalidDataException("O arquivo está vazio.");
                if (settings.Regras is null || settings.Regras.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.MatchPhrase))
                    || settings.Padroes is null || settings.Padroes.Any(pair => pair.Value is null))
                    throw new InvalidDataException("Preencha os nomes das regras e os valores padrão.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                settings = new LocalSettings();
                settingsError = $"Não foi possível ler regras.local.json. {error.Message}";
            }
        }

        Application.Run(new MainForm(settings, settingsError));
        return 0;
    }
}

internal sealed class LocalSettings
{
    public string PrefixoEquipamento { get; init; } = "";
    public OrderTitleRule[] Regras { get; init; } = [];
    public Dictionary<OrderRoute, OperationDefaults> Padroes { get; init; } = [];
}

internal sealed class OperationDefaults
{
    public string Material { get; init; } = "";
    public string Quantidade { get; init; } = "";
    public string Centro { get; init; } = "";
    public string TipoOrdem { get; init; } = "";
    public string Empresa { get; init; } = "";
    public string GrupoCompras { get; init; } = "";
    public string OrganizacaoCompras { get; init; } = "";
    public string Fornecedor { get; init; } = "";
    public string TipoDocumento { get; init; } = "";
}
