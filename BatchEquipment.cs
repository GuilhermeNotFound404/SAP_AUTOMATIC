using System.Text.RegularExpressions;

namespace SapAssistente;

internal sealed record BatchEquipment(string Equipment, string Fleet)
{
    private static readonly Regex Identifier = new(@"\A(?<prefix>[A-Za-z0-9]+-)?(?<number>[0-9]{1,40})\z", RegexOptions.CultureInvariant);

    public static IReadOnlyList<BatchEquipment> Parse(string input, string defaultPrefix)
    {
        var lines = input.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length is < 1 or > 100)
            throw new InvalidOperationException("Cole de 1 a 100 equipamentos, um por linha.");

        var equipment = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BatchEquipment>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            var match = Identifier.Match(lines[index]);
            var number = match.Groups["number"].Value;
            var fleet = number.TrimStart('0');
            if (!match.Success || fleet.Length == 0)
                throw new InvalidOperationException($"Equipamento inválido na entrada {index + 1}. Exemplo: DEMO-0000009.");
            var fullCode = match.Groups["prefix"].Success || string.IsNullOrWhiteSpace(defaultPrefix)
                ? lines[index].ToUpperInvariant()
                : defaultPrefix + number.PadLeft(7, '0');
            if (!equipment.Add(fullCode))
                throw new InvalidOperationException($"O equipamento {fullCode} aparece mais de uma vez na lista.");
            result.Add(new BatchEquipment(fullCode, fleet));
        }
        return result;
    }

    internal static void RunSelfTests()
    {
        var inputs = new[] { "9", "12", "25", "36", "41", "58", "62", "75", "86", "99", "101", "118", "126", "140", "152" };
        var codes = inputs.Select(fleet => "DEMO-" + fleet.PadLeft(7, '0')).ToArray();
        var rows = Parse(string.Join("\r\n", codes), "DEMO-");
        if (!rows.Select(row => row.Equipment).SequenceEqual(codes) || !rows.Select(row => row.Fleet).SequenceEqual(inputs))
            throw new InvalidOperationException("O lote deve manter a sequência e extrair a frota do equipamento.");
        try { Parse("DEMO-0000009\n9", "DEMO-"); }
        catch (InvalidOperationException error) when (error.Message.Contains("mais de uma vez", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("A lista deve bloquear equipamentos duplicados.");
    }
}
