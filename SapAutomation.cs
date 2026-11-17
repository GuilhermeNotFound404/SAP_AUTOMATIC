using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SapAssistente;

public sealed record SapOperation(string Name, IReadOnlyDictionary<string, string> Values, string? LinkFrom = null);

public sealed class SapRunException : InvalidOperationException
{
    internal SapRunException(string operation, Dictionary<string, string> completed, bool saveAttempted, Exception cause)
        : base($"SAP, operação {operation}. {cause.Message}", cause)
    {
        Operation = operation;
        Completed = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(completed));
        SaveAttempted = saveAttempted;
    }

    public string Operation { get; }
    public IReadOnlyDictionary<string, string> Completed { get; }
    public bool SaveAttempted { get; }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class SapAutomation
{
    private const string OrderToken = "ordem";
    private const string SaveAction = "salvar";
    private const string EasyAccess = "SESSION_MANAGER";
    private const int SaveKey = 11;
    private static readonly Regex Token = new(@"\{\{(?<chave>[A-Za-z][A-Za-z0-9]*)\}\}", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex ControlId = new(@"\Awnd\[\d+\](?:/[^\r\n]+)?\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Dictionary<string, string?> Transactions = new(StringComparer.Ordinal)
    {
        ["ordem"] = "IW31",
        ["requisicaoServico"] = "ME51N",
        ["pedidoPeca"] = null
    };
    private static readonly JsonSerializerOptions ProfileOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void Validate(string profilePath, IReadOnlyList<SapOperation> operations) =>
        Prepare(Load(profilePath), operations);

    public static string Probe(string profilePath)
    {
        Profile profile = Load(profilePath);
        Attach(profile);
        return $"{profile.Sistema} / {profile.Mandante}";
    }

    public static IReadOnlyDictionary<string, string> Run(
        string profilePath,
        IReadOnlyList<SapOperation> operations,
        Action<string> onProgress,
        Action<string, string>? onSaved = null,
        Action<string, IReadOnlyDictionary<string, string>>? beforeSave = null)
    {
        Profile profile = Load(profilePath);
        IReadOnlyList<PreparedOperation> prepared = Prepare(profile, operations);
        object session = Attach(profile);
        Call(session, "LockSessionUI");
        try
        {
            return Execute(session, prepared, onProgress, onSaved, beforeSave);
        }
        finally
        {
            try
            {
                Call(session, "UnlockSessionUI");
            }
            catch (Exception ex) when (Unwrap(ex) is COMException or MissingMemberException)
            {
                onProgress($"Não foi possível liberar a janela SAP. {Message(ex)}");
            }
        }
    }

    private static IReadOnlyDictionary<string, string> Execute(
        object session,
        IReadOnlyList<PreparedOperation> prepared,
        Action<string> onProgress,
        Action<string, string>? onSaved,
        Action<string, IReadOnlyDictionary<string, string>>? beforeSave)
    {
        var completed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (PreparedOperation operation in prepared)
        {
            bool saveAttempted = false;
            try
            {
                onProgress($"SAP {operation.Settings.Transacao} · {operation.Name}");
                if (operation.LinkFrom is { } source)
                    operation.Values[OrderToken] = completed[source];

                RequireEasyAccess(session);
                Call(session, "StartTransaction", operation.Settings.Transacao);
                WaitReady(session);
                CheckStatus(ReadStatus(session), operation.Settings);

                for (int index = 0; index < operation.Settings.Passos.Count - 1; index++)
                {
                    Step step = operation.Settings.Passos[index];
                    try
                    {
                        Apply(session, step, operation.Values);
                        WaitReady(session);
                        CheckStatus(ReadStatus(session), operation.Settings);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Passo {index + 1} ({step.Acao}). {Message(ex)}", ex);
                    }
                }

                RequireWindow(session, "wnd[0]");
                if (!string.Equals(ReadString(ReadObject(session, "Info"), "Transaction"), operation.Settings.Transacao, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A transação ativa diverge da transação configurada.");

                object window = Call(session, "FindById", "wnd[0]");
                if (!Convert.ToBoolean(Call(window, "IsVKeyAllowed", (short)SaveKey), CultureInfo.InvariantCulture))
                    throw new InvalidOperationException("A opção de gravar está indisponível nesta tela SAP.");
                SapStatus priorStatus = ReadStatus(session);
                beforeSave?.Invoke(operation.Name, new ReadOnlyDictionary<string, string>(operation.Values));
                saveAttempted = true;
                Call(window, "SendVKey", (short)SaveKey);
                WaitReady(session);
                SapStatus afterSave = ReadStatus(session);
                CheckStatus(afterSave, operation.Settings);
                string number = DocumentNumber(operation, priorStatus, afterSave);
                completed.Add(operation.Name, number);
                onSaved?.Invoke(operation.Name, number);
                ReturnToEasyAccess(session, operation.Settings.Transacao, onProgress);
            }
            catch (Exception ex)
            {
                throw new SapRunException(operation.Name, completed, saveAttempted, Unwrap(ex));
            }
        }

        return new ReadOnlyDictionary<string, string>(completed);
    }

    private static Profile Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("Selecione um perfil SAP local com o mapeamento das suas telas.");

        Profile profile;
        try
        {
            profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), ProfileOptions)
                ?? throw new InvalidOperationException("O perfil SAP está vazio.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"JSON inválido no perfil SAP ({ex.Path}).", ex);
        }

        if (string.IsNullOrWhiteSpace(profile.Sistema) || !Regex.IsMatch(profile.Mandante ?? "", @"\A[0-9]{3}\z"))
            throw new InvalidOperationException("Informe o sistema SAP (SID) e o mandante de 3 dígitos no perfil.");
        if (profile.Operacoes is null)
            throw new InvalidOperationException("A propriedade operacoes do perfil SAP deve ser um objeto.");
        return profile;
    }

    private static IReadOnlyList<PreparedOperation> Prepare(Profile profile, IReadOnlyList<SapOperation> operations)
    {
        if (operations.Count == 0)
            throw new InvalidOperationException("Escolha pelo menos uma operação SAP.");

        List<PreparedOperation> prepared = new();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (SapOperation operation in operations)
        {
            string name = operation.Name;
            if (!Transactions.TryGetValue(name, out string? expectedTransaction))
                throw new InvalidOperationException($"Operação SAP desconhecida: {name}.");
            if (names.Contains(name))
                throw new InvalidOperationException($"A operação {name} aparece mais de uma vez.");
            if (operation.LinkFrom is { } source && (source != "ordem" || !names.Contains(source)))
                throw new InvalidOperationException($"A operação {name} pode usar o número de uma ordem criada no mesmo lote.");
            names.Add(name);

            if (!profile.Operacoes.TryGetValue(name, out OperationSettings? settings) || settings is null)
                throw new InvalidOperationException($"Falta mapear a operação {name} no perfil local.");
            if (!Regex.IsMatch(settings.Transacao ?? "", @"\A[A-Z][A-Z0-9_]{1,19}\z") ||
                (expectedTransaction is not null && settings.Transacao != expectedTransaction))
                throw new InvalidOperationException($"Transação inválida para {name} no perfil SAP.");
            if (settings.Passos is null || settings.Passos.Count < 2 || settings.Passos[^1]?.Acao != SaveAction ||
                settings.Passos.Take(settings.Passos.Count - 1).Any(step => step?.Acao == SaveAction))
                throw new InvalidOperationException($"Configure os passos de {name} e encerre com uma única ação salvar.");
            if (string.IsNullOrWhiteSpace(settings.MensagemClasse) || settings.MensagemClasse.Contains("SUBSTITUA", StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(settings.MensagemNumero ?? "", @"\A[0-9]{3}\z") || string.IsNullOrWhiteSpace(settings.NumeroRegex))
                throw new InvalidOperationException($"Configure a classe, o código da mensagem de sucesso e o padrão do número de {name}.");
            Regex numberPattern = CompileNumberPattern(settings.NumeroRegex, name);

            var values = new Dictionary<string, string>(settings.ValoresFixos ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach ((string key, string value) in operation.Values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    values[key] = value;
            }
            if (operation.LinkFrom is not null)
                values[OrderToken] = "00000000000";

            for (int index = 0; index < settings.Passos.Count - 1; index++)
                ValidateStep(settings.Passos[index], values, name, index + 1);
            RequireMappings(name, settings.Passos);

            prepared.Add(new PreparedOperation(name, operation.LinkFrom, values, settings, numberPattern));
        }
        return prepared;
    }

    private static void RequireMappings(string name, IEnumerable<Step> steps)
    {
        string[] required = name switch
        {
            "ordem" => ["equipamento", "titulo"],
            "requisicaoServico" => ["ordem", "material", "quantidade", "preco", "frota"],
            "pedidoPeca" => ["material", "quantidade"],
            _ => []
        };
        var mapped = steps.Where(step => step.Acao is "texto" or "chave" or "celula")
            .SelectMany(step => Token.Matches(step.Valor!))
            .Select(match => match.Groups["chave"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] missing = required.Where(field => !mapped.Contains(field)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Mapeie os seguintes campos nos passos da operação {name}: {string.Join(", ", missing)}.");
    }

    private static Regex CompileNumberPattern(string pattern, string operation)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"O numeroRegex da operação {operation} é inválido.", ex);
        }

        if (!regex.GetGroupNames().Contains("numero", StringComparer.Ordinal))
            throw new InvalidOperationException($"O numeroRegex da operação {operation} precisa do grupo nomeado numero.");
        return regex;
    }

    private static void ValidateStep(Step? step, IReadOnlyDictionary<string, string> values, string operation, int index)
    {
        if (step?.Id is not { } id || !ControlId.IsMatch(id) || id.Contains("SUBSTITUA", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("/okcd", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("/pwd", StringComparison.OrdinalIgnoreCase) || id.EndsWith("/tbar[0]/btn[11]", StringComparison.Ordinal))
            throw new InvalidOperationException($"ID inválido no passo {index} de {operation}.");

        switch (step.Acao)
        {
            case "texto":
            case "chave":
            case "tabelaLinha":
                Expand(step.Valor, values);
                break;
            case "celula":
                if (step.Linha is null or < 0 || string.IsNullOrWhiteSpace(step.Coluna))
                    throw new InvalidOperationException($"Informe linha e coluna no passo {index} de {operation}.");
                Expand(step.Valor, values);
                break;
            case "rolar":
                if (step.Linha is null or < 0)
                    throw new InvalidOperationException($"Informe a linha no passo {index} de {operation}.");
                break;
            case "tecla":
                if (id.Contains('/') || step.Tecla is null or < 0 or > 93 or SaveKey)
                    throw new InvalidOperationException($"Tecla inválida no passo {index} de {operation}.");
                break;
            case "marcar":
                if (step.Marcado is null)
                    throw new InvalidOperationException($"Informe marcado no passo {index} de {operation}.");
                break;
            case "pressionar":
            case "focar":
            case "selecionar":
            case "validarGrade":
                break;
            default:
                throw new InvalidOperationException($"Ação desconhecida no passo {index} de {operation}: {step.Acao}.");
        }
    }

    private static object Attach(Profile profile)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("A automação SAP precisa de uma thread STA.");

        try
        {
            Type? wrapperType = Type.GetTypeFromProgID("SapROTWr.SapROTWrapper");
            if (wrapperType is null)
                throw new InvalidOperationException("O componente SAP GUI Scripting está indisponível. Confira a instalação e a arquitetura do SAP GUI.");
            object wrapper = Activator.CreateInstance(wrapperType)!;
            object? runningSap = Invoke(wrapper, "GetROTEntry", BindingFlags.InvokeMethod, "SAPGUI");
            if (runningSap is null)
                throw new InvalidOperationException("Abra o SAP GUI para Windows e entre no sistema antes de continuar.");

            object application = Call(runningSap, "GetScriptingEngine");
            object connections = ReadObject(application, "Connections");
            object? selected = null;
            for (int i = 0; i < ReadInt(connections, "Count"); i++)
            {
                object connection = Call(connections, "Item", i);
                if (Convert.ToBoolean(ReadObject(connection, "DisabledByServer"), CultureInfo.InvariantCulture))
                    continue;
                object sessions = ReadObject(connection, "Sessions");
                for (int j = 0; j < ReadInt(sessions, "Count"); j++)
                {
                    object session = Call(sessions, "Item", j);
                    if (!string.IsNullOrWhiteSpace(profile.Sessao) && !MatchesSessionId(session, profile.Sessao))
                        continue;

                    object info = ReadObject(session, "Info");
                    if (!string.Equals(ReadString(info, "SystemName"), profile.Sistema, StringComparison.OrdinalIgnoreCase) ||
                        ReadString(info, "Client") != profile.Mandante || string.IsNullOrWhiteSpace(ReadString(info, "User")))
                        continue;
                    if (selected is not null)
                        throw new InvalidOperationException("Há mais de uma sessão SAP para este sistema e mandante. Deixe apenas a sessão desejada aberta ou informe sessao no perfil.");
                    selected = session;
                }
            }

            if (selected is null)
                throw new InvalidOperationException("Nenhuma sessão SAP autenticada e com scripting habilitado corresponde ao perfil.");

            WaitReady(selected);
            RequireEasyAccess(selected);
            return selected;
        }
        catch (Exception ex) when (Unwrap(ex) is COMException or MissingMemberException)
        {
            throw new InvalidOperationException($"Falha de acesso ao SAP GUI Scripting. {Message(ex)}", Unwrap(ex));
        }
    }

    private static bool MatchesSessionId(object session, string configured)
    {
        string id = ReadString(session, "Id");
        return id == configured || id == "/app/" + configured;
    }

    private static void Apply(object session, Step step, IReadOnlyDictionary<string, string> values)
    {
        string id = step.Id!;
        string window = id[..(id.IndexOf(']') + 1)];
        RequireWindow(session, window);
        object control = Call(session, "FindById", id);
        switch (step.Acao)
        {
            case "texto": Set(control, "Text", Expand(step.Valor, values)); break;
            case "chave": Set(control, "Key", Expand(step.Valor, values)); break;
            case "marcar": Set(control, "Selected", step.Marcado!.Value); break;
            case "pressionar": Call(control, "Press"); break;
            case "tecla": Call(control, "SendVKey", (short)step.Tecla!.Value); break;
            case "focar": Call(control, "SetFocus"); break;
            case "selecionar": Call(control, "Select"); break;
            case "rolar": Set(control, "FirstVisibleRow", step.Linha!.Value); break;
            case "tabelaLinha": Set(control, "SelectedRows", Expand(step.Valor, values)); break;
            case "celula": Call(control, "ModifyCell", step.Linha!.Value, step.Coluna!, Expand(step.Valor, values)); break;
            case "validarGrade": Call(control, "TriggerModified"); break;
        }
    }

    private static string Expand(string? template, IReadOnlyDictionary<string, string> values)
    {
        if (template is null)
            throw new InvalidOperationException("Falta o valor do passo no perfil SAP.");
        string literals = Token.Replace(template, "");
        if (literals.Contains("{{", StringComparison.Ordinal) || literals.Contains("}}", StringComparison.Ordinal))
            throw new InvalidOperationException("Use o formato {{campo}} para as variáveis do perfil SAP.");
        return Token.Replace(template, match =>
        {
            string key = match.Groups["chave"].Value;
            if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Falta preencher o campo {key} para a operação SAP.");
            return value;
        });
    }

    private static void RequireWindow(object session, string expected)
    {
        string id = ReadString(ReadObject(session, "ActiveWindow"), "Id");
        if (id != expected && !id.EndsWith("/" + expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"O SAP abriu uma janela diferente de {expected}. Confira a tela e o perfil antes de continuar.");
    }

    private static void RequireEasyAccess(object session)
    {
        RequireWindow(session, "wnd[0]");
        if (!string.Equals(ReadString(ReadObject(session, "Info"), "Transaction"), EasyAccess, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Volte à tela inicial SAP Easy Access na sessão escolhida antes de iniciar a operação.");
    }

    private static void ReturnToEasyAccess(object session, string completedTransaction, Action<string> onProgress)
    {
        try
        {
            RequireWindow(session, "wnd[0]");
            string active = ReadString(ReadObject(session, "Info"), "Transaction");
            if (string.Equals(active, EasyAccess, StringComparison.OrdinalIgnoreCase))
                return;
            if (!string.Equals(active, completedTransaction, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A transação ativa mudou após a gravação.");

            Call(session, "EndTransaction");
            WaitReady(session);
            RequireEasyAccess(session);
        }
        catch (Exception ex)
        {
            onProgress($"Documento gravado. Confira o SAP Easy Access antes da próxima operação. {Message(ex)}");
        }
    }

    private static void WaitReady(object session)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (Convert.ToBoolean(ReadObject(session, "Busy"), CultureInfo.InvariantCulture))
        {
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(40))
                throw new TimeoutException("O SAP continuou ocupado por mais de 40 segundos.");
            Thread.Sleep(75);
        }
    }

    private static SapStatus ReadStatus(object session)
    {
        object bar = Call(session, "FindById", "wnd[0]/sbar");
        return new SapStatus(ReadString(bar, "MessageType"), ReadString(bar, "MessageId"),
            ReadString(bar, "MessageNumber").PadLeft(3, '0'), ReadString(bar, "Text"));
    }

    private static void CheckStatus(SapStatus status, OperationSettings settings)
    {
        if (status.Type is "E" or "A" || (status.Type == "W" &&
            !(settings.AvisosPermitidos?.Contains($"{status.MessageClass}:{status.Number}", StringComparer.Ordinal) ?? false)))
            throw new InvalidOperationException($"Mensagem SAP {status.Type} {status.MessageClass}:{status.Number}. {status.Text}");
    }

    private static string DocumentNumber(PreparedOperation operation, SapStatus beforeSave, SapStatus afterSave)
    {
        if (beforeSave == afterSave || afterSave.Type != "S" ||
            !string.Equals(afterSave.MessageClass, operation.Settings.MensagemClasse, StringComparison.Ordinal) ||
            afterSave.Number != operation.Settings.MensagemNumero)
            throw new InvalidOperationException($"O SAP não confirmou a gravação com a mensagem esperada. Mensagem recebida: {afterSave.Text}");

        MatchCollection matches = operation.NumberPattern.Matches(afterSave.Text);
        Group? result = matches.Count == 1 ? matches[0].Groups["numero"] : null;
        if (result is not { Success: true } || !Regex.IsMatch(result.Value, @"\A[0-9]{4,20}\z"))
            throw new InvalidOperationException("A mensagem do SAP confirmou a gravação, mas o número do documento não pôde ser lido. Confira no SAP antes de repetir.");
        return result.Value;
    }

    private static object? Invoke(object target, string name, BindingFlags flags, params object[] values) =>
        target.GetType().InvokeMember(name, flags | BindingFlags.OptionalParamBinding, null, target, values, CultureInfo.InvariantCulture);

    private static object Call(object target, string name, params object[] values) =>
        Invoke(target, name, BindingFlags.InvokeMethod, values)!;

    private static object ReadObject(object target, string name) =>
        Invoke(target, name, BindingFlags.GetProperty)!;

    private static string ReadString(object target, string name) =>
        Convert.ToString(ReadObject(target, name), CultureInfo.InvariantCulture) ?? "";

    private static int ReadInt(object target, string name) =>
        Convert.ToInt32(ReadObject(target, name), CultureInfo.InvariantCulture);

    private static void Set(object target, string name, object value) =>
        Invoke(target, name, BindingFlags.SetProperty, value);

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;

    private static string Message(Exception exception) => Unwrap(exception).Message;

    private sealed record PreparedOperation(string Name, string? LinkFrom, Dictionary<string, string> Values,
        OperationSettings Settings, Regex NumberPattern);

    private sealed record SapStatus(string Type, string MessageClass, string Number, string Text);

    private sealed class Profile
    {
        public string Sistema { get; init; } = "";
        public string Mandante { get; init; } = "";
        public string? Sessao { get; init; }
        public Dictionary<string, OperationSettings> Operacoes { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class OperationSettings
    {
        public string Transacao { get; init; } = "";
        public string MensagemClasse { get; init; } = "";
        public string MensagemNumero { get; init; } = "";
        public string NumeroRegex { get; init; } = "";
        public List<Step> Passos { get; init; } = new();
        public Dictionary<string, string>? ValoresFixos { get; init; }
        public List<string>? AvisosPermitidos { get; init; }
    }

    private sealed class Step
    {
        public string Acao { get; init; } = "";
        public string? Id { get; init; }
        public string? Valor { get; init; }
        public int? Tecla { get; init; }
        public bool? Marcado { get; init; }
        public int? Linha { get; init; }
        public string? Coluna { get; init; }
    }
}
