using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SapAssistente;

internal sealed class OperationJournal(string directory, DateOnly day)
{
    private const string Pending = "PENDENTE";

    public string? Read(SapOperation operation)
    {
        var path = EntryPath(operation);
        if (!File.Exists(path)) return null;
        var contents = File.ReadAllText(path);
        return contents.StartsWith('{') ? ReadSheetRow(contents).Ordem : contents;
    }

    public void Begin(SapOperation operation)
    {
        Directory.CreateDirectory(directory);
        using var file = new FileStream(EntryPath(operation), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(Encoding.UTF8.GetBytes(Pending));
        file.Flush(flushToDisk: true);
    }

    public void Record(SapOperation operation, string number, OrderSheetRow? sheetRow = null)
    {
        var path = EntryPath(operation);
        var temp = path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(sheetRow is null ? Encoding.UTF8.GetBytes(number) : JsonSerializer.SerializeToUtf8Bytes(sheetRow));
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public void CancelBeforeSave(SapOperation operation) => File.Delete(EntryPath(operation));

    public IEnumerable<(string Path, OrderSheetRow Row)> PendingSheetRows()
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var path in Directory.EnumerateFiles(directory, "*-ordem-*.txt").Order(StringComparer.Ordinal))
        {
            if (File.Exists(path + ".planilha")) continue;
            var contents = File.ReadAllText(path);
            if (contents.StartsWith('{')) yield return (path, ReadSheetRow(contents));
        }
    }

    public static void MarkSheetSent(string path)
    {
        using var marker = new FileStream(path + ".planilha", FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        marker.Flush(flushToDisk: true);
    }

    private static OrderSheetRow ReadSheetRow(string json) => JsonSerializer.Deserialize<OrderSheetRow>(json)
        ?? throw new InvalidDataException("Registro da ordem inválido no histórico local.");

    public static bool IsPending(string value) => value == Pending || string.IsNullOrWhiteSpace(value);

    private string EntryPath(SapOperation operation)
    {
        var values = operation.Values;
        var identity = string.Join('\u001f',
            day.ToString("yyyy-MM-dd"), operation.Name,
            values.GetValueOrDefault("titulo")?.Trim().Normalize().ToUpperInvariant(),
            values.GetValueOrDefault("equipamento")?.Trim().ToUpperInvariant(),
            operation.Name == "ordem" ? "" : values.GetValueOrDefault("ordem"),
            operation.Name == "pedidoPeca" ? values.GetValueOrDefault("material") : "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(directory, $"{day:yyyy-MM-dd}-{operation.Name}-{hash[..20]}.txt");
    }

    internal static void RunSelfTests()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sap-assistente-teste-{Guid.NewGuid():N}");
        var journal = new OperationJournal(directory, new DateOnly(2026, 1, 2));
        var order = new SapOperation("ordem", new Dictionary<string, string> { ["titulo"] = "Teste", ["equipamento"] = "0001" });
        try
        {
            journal.Begin(order);
            if (journal.Read(order) is not { } pending || !IsPending(pending))
                throw new InvalidOperationException("A tentativa pendente deveria estar registrada.");
            try
            {
                journal.Begin(order);
                throw new InvalidOperationException("Uma segunda tentativa deveria ser bloqueada.");
            }
            catch (IOException) { }
            journal.Record(order, "000123");
            if (journal.Read(order) != "000123")
                throw new InvalidOperationException("Número confirmado incorreto.");
            if (journal.PendingSheetRows().Any())
                throw new InvalidOperationException("O histórico anterior deve continuar legível e sem envio retroativo.");
            var row = new OrderSheetRow("000123", "2026-01-02", "0001", "Empresa teste", "Aberta", "Serviço", "Teste");
            journal.Record(order, row.Ordem, row);
            var anotherDay = new OperationJournal(directory, new DateOnly(2026, 1, 3));
            var pendingRow = anotherDay.PendingSheetRows().Single();
            if (journal.Read(order) != row.Ordem || pendingRow.Row != row)
                throw new InvalidOperationException("A ordem e a fila precisam persistir juntas entre aberturas.");
            MarkSheetSent(pendingRow.Path);
            if (anotherDay.PendingSheetRows().Any() || journal.Read(order) != row.Ordem)
                throw new InvalidOperationException("Confirmar o envio não deve remover o histórico da ordem.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
