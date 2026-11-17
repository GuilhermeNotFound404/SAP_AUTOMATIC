using System.Security.Cryptography;
using System.Text;

namespace SapAssistente;

internal sealed class OperationJournal(string directory, DateOnly day)
{
    private const string Pending = "PENDENTE";

    public string? Read(SapOperation operation)
    {
        var path = EntryPath(operation);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Begin(SapOperation operation)
    {
        Directory.CreateDirectory(directory);
        using var file = new FileStream(EntryPath(operation), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(Encoding.UTF8.GetBytes(Pending));
        file.Flush(flushToDisk: true);
    }

    public void Record(SapOperation operation, string number)
    {
        var path = EntryPath(operation);
        var temp = path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.UTF8.GetBytes(number));
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
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
