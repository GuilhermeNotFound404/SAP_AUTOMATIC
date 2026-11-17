using System.Globalization;

namespace SapAssistente;

internal sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(26, 40, 56);
    private static readonly CultureInfo Brazilian = CultureInfo.GetCultureInfo("pt-BR");
    private readonly LocalSettings settings;
    private readonly TableLayoutPanel form = new();
    private readonly TextBox title = new();
    private readonly TextBox fleet = new();
    private readonly TextBox equipment = new();
    private readonly ComboBox route = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox damage = new();
    private readonly TextBox collection = new();
    private readonly TextBox noteGroup = new();
    private readonly TextBox noteCode = new();
    private readonly TextBox material = new();
    private readonly TextBox quantity = new();
    private readonly TextBox price = new();
    private readonly TextBox existingOrder = new();
    private readonly TextBox company = new() { MaxLength = 120 };
    private readonly TextBox sheetStatus = new() { MaxLength = 60 };
    private readonly TextBox sheetType = new() { MaxLength = 80 };
    private readonly TextBox profile = new() { ReadOnly = true };
    private readonly Label suggestion = new() { AutoSize = true, ForeColor = Color.FromArgb(44, 85, 118) };
    private readonly TextBox status = new() { ReadOnly = true, Multiline = true, Height = 116, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
    private readonly Button execute = new() { Text = "Revisar e executar", AutoSize = true };
    private readonly Button browse = new() { Text = "Escolher perfil…", AutoSize = true };
    private bool busy;
    private bool applyingSuggestion;

    public MainForm(LocalSettings settings, string? settingsError, bool syncOnLaunch = true)
    {
        this.settings = settings;
        Text = "Assistente SAP";
        Font = new Font("Segoe UI", 10F);
        ForeColor = Ink;
        BackColor = Color.FromArgb(244, 247, 250);
        MinimumSize = new Size(560, 600);
        ClientSize = new Size(810, 740);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 14, 24, 14) };
        Controls.Add(scroll);
        form.Dock = DockStyle.Top;
        form.AutoSize = true;
        form.ColumnCount = 2;
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        scroll.Controls.Add(form);

        AddFull(new Label { Text = "Assistente SAP", Font = new Font("Segoe UI", 22F, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 12) });
        title.PlaceholderText = "Ex.: SERV. BORRACHARIA";
        AddField("Nome da ordem ou solicitação", title);
        var classify = new Button { Text = "Identificar pelo nome", AutoSize = true, Anchor = AnchorStyles.Left };
        classify.Click += (_, _) => ApplySuggestion();
        AddField("", classify, stretch: false);
        AddField("", suggestion);
        route.Items.AddRange(Enum.GetValues<OrderRoute>().Select(value => new RouteOption(value, OrderRules.DisplayName(value))).ToArray());
        AddField("Fluxo", route);
        AddField("Frota", fleet);
        AddField("Equipamento SAP", equipment);
        AddField("Avaria", damage);
        AddField("Tipo de recolhimento", collection);
        AddField("Grupo de codificação da nota", noteGroup);
        AddField("Código da nota", noteCode);
        AddField("Material", material);
        AddField("Quantidade", quantity);
        price.PlaceholderText = "Ex.: 120,00";
        AddField("Preço de avaliação (R$)", price);
        existingOrder.PlaceholderText = "Preencha para vincular uma ordem já criada";
        AddField("Ordem existente", existingOrder);
        AddField("Empresa", company);
        AddField("Status da ordem na planilha", sheetStatus);
        AddField("Tipo da ordem na planilha", sheetType);
        var profilePicker = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        profilePicker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        profilePicker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        profile.Dock = DockStyle.Fill;
        profilePicker.Controls.Add(profile, 0, 0);
        profilePicker.Controls.Add(browse, 1, 0);
        AddField("Perfil das telas do SAP", profilePicker);
        var localProfile = Path.Combine(AppContext.BaseDirectory, "sap.local.json");
        if (File.Exists(localProfile)) profile.Text = localProfile;

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 6) };
        execute.Padding = new Padding(10, 4, 10, 4);
        actions.Controls.Add(execute);
        var batch = new Button { Text = "Criar ordens em lote", AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
        batch.Click += async (_, _) => await ReviewAndExecuteBatch();
        actions.Controls.Add(batch);
        var probe = new Button { Text = "Verificar conexão SAP", AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
        probe.Click += async (_, _) => await CheckConnection();
        actions.Controls.Add(probe);
        var sync = new Button { Text = "Sincronizar planilha", AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
        sync.Click += async (_, _) => await SyncSheet(requested: true);
        actions.Controls.Add(sync);
        var copy = new Button { Text = "Copiar resultado", AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
        actions.Controls.Add(copy);
        AddFull(actions);
        AddField("Resultado", status);

        route.SelectedValueChanged += (_, _) => { if (!applyingSuggestion) ApplyDefaults(); UpdateFields(); };
        existingOrder.TextChanged += (_, _) => UpdateFields();
        fleet.TextChanged += (_, _) => FillEquipment();
        title.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            ApplySuggestion();
        };
        title.TextChanged += (_, _) =>
        {
            existingOrder.Clear();
            suggestion.Text = "Identifique o fluxo pelo nome ou escolha manualmente.";
        };
        browse.Click += (_, _) => ChooseProfile();
        execute.Click += async (_, _) => await ReviewAndExecute();
        copy.Click += (_, _) => { if (status.TextLength > 0) Clipboard.SetText(status.Text); };
        FormClosing += (_, e) => e.Cancel = busy;
        if (syncOnLaunch) Shown += async (_, _) => await SyncSheet();
        ApplySuggestion();
        if (settingsError is not null) WriteStatus(settingsError);
    }

    private OrderRoute SelectedRoute => (route.SelectedItem as RouteOption)?.Value ?? OrderRoute.Manual;

    private OperationDefaults Defaults => settings.Padroes.GetValueOrDefault(SelectedRoute) ?? new();

    private void ApplySuggestion()
    {
        var match = OrderRules.Suggest(title.Text, settings.Regras);
        applyingSuggestion = true;
        route.SelectedIndex = (int)match.Route;
        applyingSuggestion = false;
        damage.Text = match.Damage ?? "";
        collection.Text = match.CollectionType ?? "";
        noteGroup.Text = match.NoteCodeGroup ?? "";
        noteCode.Text = match.NoteCode ?? "";
        suggestion.Text = match.Reason;
        ApplyDefaults();
        UpdateFields();
    }

    private void ApplyDefaults()
    {
        material.Text = Defaults.Material;
        quantity.Text = Defaults.Quantidade;
        company.Text = Defaults.Empresa;
        sheetStatus.Text = Defaults.StatusPlanilha;
        sheetType.Text = Defaults.TipoPlanilha;
    }

    private void UpdateFields()
    {
        var needOrder = (SelectedRoute is OrderRoute.ServiceOrder or OrderRoute.ServiceOrderAndRequest) && existingOrder.TextLength == 0;
        noteGroup.Enabled = noteCode.Enabled = needOrder;
        company.Enabled = sheetStatus.Enabled = sheetType.Enabled = needOrder;
        material.Enabled = quantity.Enabled = SelectedRoute is OrderRoute.ServiceOrderAndRequest or OrderRoute.PartRequest;
        price.Enabled = material.Enabled;
        existingOrder.Enabled = SelectedRoute is OrderRoute.ServiceOrderAndRequest or OrderRoute.PartRequest;
        if (!existingOrder.Enabled && existingOrder.TextLength > 0) existingOrder.Clear();
    }

    private void FillEquipment()
    {
        var previous = (string?)equipment.Tag;
        if (equipment.TextLength > 0 && equipment.Text != previous) return;
        var value = fleet.Text.Trim();
        var generated = string.IsNullOrWhiteSpace(settings.PrefixoEquipamento) || value.Length == 0
            ? "" : $"{settings.PrefixoEquipamento}{value.PadLeft(7, '0')}";
        equipment.Tag = generated;
        equipment.Text = generated;
    }

    private void ChooseProfile()
    {
        using var picker = new OpenFileDialog { Filter = "Perfil SAP (*.json)|*.json", Title = "Escolher perfil local do SAP" };
        if (picker.ShowDialog(this) == DialogResult.OK) profile.Text = picker.FileName;
    }

    private async Task CheckConnection()
    {
        if (busy) return;
        try
        {
            var path = profile.Text;
            if (path.Length == 0) throw new InvalidOperationException("Escolha um perfil das telas antes de verificar a conexão.");
            busy = true;
            var session = await OnSta(() => SapAutomation.Probe(path));
            WriteStatus($"Sessão SAP encontrada: {session}.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            WriteStatus(error.Message);
        }
        finally
        {
            busy = false;
        }
    }

    private List<SapOperation> BuildPlan(BatchEquipment? item = null)
    {
        var flow = item is null ? SelectedRoute : OrderRoute.ServiceOrder;
        var fleetId = item?.Fleet ?? fleet.Text.Trim();
        var equipmentId = item?.Equipment ?? equipment.Text.Trim();
        if (flow == OrderRoute.Manual) throw new InvalidOperationException("Escolha o fluxo a executar.");
        if (title.TextLength == 0 || string.IsNullOrWhiteSpace(title.Text)) throw new InvalidOperationException("Digite o nome da ordem ou solicitação.");
        if (fleetId.Length == 0 || equipmentId.Length == 0) throw new InvalidOperationException("Informe a frota e o equipamento SAP.");
        if (item is null && (string.IsNullOrWhiteSpace(damage.Text) || string.IsNullOrWhiteSpace(collection.Text)))
            throw new InvalidOperationException("Informe a avaria e o tipo de recolhimento.");
        var order = item is null ? existingOrder.Text.Trim() : "";
        if (order.Any(c => !char.IsAsciiDigit(c))) throw new InvalidOperationException("A ordem existente deve conter apenas números.");
        var createsOrder = flow == OrderRoute.ServiceOrder || (flow == OrderRoute.ServiceOrderAndRequest && order.Length == 0);
        if (createsOrder && (!SheetText(company.Text, 120) || !SheetText(sheetStatus.Text, 60) || !SheetText(sheetType.Text, 80) ||
            !SheetText(fleetId, 50) || !SheetText(title.Text, 250)))
            throw new InvalidOperationException("Para a planilha, informe empresa, status, tipo, frota e nome da ordem dentro dos limites dos campos.");
        var isRequisition = flow is OrderRoute.ServiceOrderAndRequest or OrderRoute.PartRequest;
        if (isRequisition && (string.IsNullOrWhiteSpace(material.Text) || !PositiveNumber(quantity.Text))) throw new InvalidOperationException("Informe o material e uma quantidade positiva.");
        if ((flow == OrderRoute.ServiceOrderAndRequest || (isRequisition && price.TextLength > 0)) && !PositiveNumber(price.Text)) throw new InvalidOperationException("Informe um preço de avaliação positivo, como 120,00.");

        var defaults = Defaults;
        var values = new Dictionary<string, string>
        {
            ["titulo"] = title.Text.Trim(), ["frota"] = fleetId, ["equipamento"] = equipmentId,
            ["avaria"] = damage.Text.Trim(), ["recolhimento"] = collection.Text.Trim(),
            ["grupoNota"] = noteGroup.Text.Trim(), ["codigoNota"] = noteCode.Text.Trim(), ["ordem"] = order,
            ["material"] = material.Text.Trim(), ["quantidade"] = quantity.Text.Trim(), ["preco"] = price.Text.Trim(),
            ["centro"] = defaults.Centro, ["tipoOrdem"] = defaults.TipoOrdem, ["empresa"] = company.Text.Trim(),
            ["statusPlanilha"] = sheetStatus.Text.Trim(), ["tipoPlanilha"] = sheetType.Text.Trim(),
            ["grupoCompras"] = defaults.GrupoCompras, ["organizacaoCompras"] = defaults.OrganizacaoCompras,
            ["fornecedor"] = defaults.Fornecedor, ["tipoDocumento"] = defaults.TipoDocumento
        };
        return flow switch
        {
            OrderRoute.ServiceOrder => [new("ordem", values)],
            OrderRoute.ServiceOrderAndRequest when order.Length == 0 => [new("ordem", values), new("requisicaoServico", values, "ordem")],
            OrderRoute.ServiceOrderAndRequest => [new("requisicaoServico", values)],
            OrderRoute.PartRequest => [new("pedidoPeca", values)],
            _ => throw new InvalidOperationException("Fluxo inválido.")
        };
    }

    private async Task ReviewAndExecuteBatch()
    {
        if (busy) return;
        try
        {
            if (title.Text.Trim().Length == 0) throw new InvalidOperationException("Digite o nome que será usado em todas as ordens.");
            if (SelectedRoute == OrderRoute.Manual) ApplySuggestion();
            var path = profile.Text;
            if (path.Length == 0) throw new InvalidOperationException("Escolha o perfil SAP local com a IW31 mapeada.");
            using var input = new Form
            {
                Text = "Criar ordens em lote", ClientSize = new Size(610, 430), StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, Font = Font, FormBorderStyle = FormBorderStyle.FixedDialog
            };
            var list = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MaxLength = 6000 };
            var confirmation = new Button { Text = "Revisar lote", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.AddRange([confirmation, cancel]);
            input.Controls.Add(list);
            input.Controls.Add(buttons);
            input.Controls.Add(new Label { Text = "Cole um código de equipamento por linha.", Dock = DockStyle.Top, Height = 36, Padding = new Padding(8) });
            input.CancelButton = cancel;
            if (input.ShowDialog(this) != DialogResult.OK) return;

            var equipmentList = BatchEquipment.Parse(list.Text, settings.PrefixoEquipamento);
            var plans = equipmentList.Select(BuildPlan).ToList();
            foreach (var plan in plans) SapAutomation.Validate(path, plan);
            var fields = plans[0][0].Values;
            var summary = $"Abrir até {plans.Count} ordens na IW31, uma por equipamento.\n\n" +
                $"Nome: {fields["titulo"]}\nTipo de ordem: {fields["tipoOrdem"]}\nCentro: {fields["centro"]}\n" +
                $"Codificação: {fields["grupoNota"]} / {fields["codigoNota"]}\n" +
                $"Planilha Ordens: {fields["empresa"]} · {fields["statusPlanilha"]} · {fields["tipoPlanilha"]}\n\n" +
                string.Join(", ", equipmentList.Take(10).Select(e => e.Fleet)) +
                (plans.Count > 10 ? $" e mais {plans.Count - 10} frotas" : "") + "\n\nExecutar o lote agora?";
            if (MessageBox.Show(this, summary, "Revisar lote de ordens", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            busy = true;
            form.Enabled = false;
            status.Clear();
            if (form.Parent is ScrollableControl scroll) scroll.ScrollControlIntoView(status);
            var progress = new Progress<string>(WriteStatus);
            var journal = LocalJournal();
            var sheetAvailable = true;
            var confirmed = 0;
            foreach (var plan in plans)
            {
                var fleetId = plan[0].Values["frota"];
                WriteStatus($"Frota {fleetId} · {confirmed + 1} de {plans.Count}");
                try { await OnSta(() => RunPlan(path, plan, journal, progress)); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    WriteStatus($"Lote interrompido na frota {fleetId}. {error.Message}");
                    if (sheetAvailable) await PublishSheet(journal, progress);
                    return;
                }
                confirmed++;
                if (sheetAvailable) sheetAvailable = await PublishSheet(journal, progress);
            }
            WriteStatus($"Lote SAP concluído. {confirmed} de {plans.Count} ordens confirmadas no histórico.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            WriteStatus(error.Message);
        }
        finally
        {
            busy = false;
            form.Enabled = true;
        }
    }

    private async Task ReviewAndExecute()
    {
        if (busy) return;
        OperationJournal? journal = null;
        var progress = new Progress<string>(WriteStatus);
        try
        {
            var plan = BuildPlan();
            var path = profile.Text;
            if (path.Length == 0) throw new InvalidOperationException("Escolha um perfil das telas antes de executar no SAP.");
            SapAutomation.Validate(path, plan);
            var fields = plan[0].Values;
            var lines = new List<string>
            {
                $"Fluxo: {OrderRules.DisplayName(SelectedRoute)}", $"Nome: {fields["titulo"]}",
                $"Frota / equipamento: {fields["frota"]} / {fields["equipamento"]}", $"Perfil: {Path.GetFileName(path)}",
                $"Avaria / recolhimento: {fields["avaria"]} / {fields["recolhimento"]}"
            };
            foreach (var (key, label) in new (string, string)[]
            {
                ("ordem", "Ordem existente"), ("centro", "Centro"), ("tipoOrdem", "Tipo de ordem"),
                ("grupoNota", "Grupo da nota"), ("codigoNota", "Código da nota"), ("material", "Material"),
                ("quantidade", "Quantidade"), ("preco", "Preço de avaliação (R$)"), ("fornecedor", "Fornecedor"),
                ("empresa", "Empresa"), ("statusPlanilha", "Status na planilha"), ("tipoPlanilha", "Tipo na planilha")
            })
                if (fields[key].Length > 0) lines.Add($"{label}: {fields[key]}");
            lines.Add("\nGravar os documentos no SAP agora?");
            if (MessageBox.Show(this, string.Join(Environment.NewLine, lines), "Revisar envio ao SAP", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            busy = true;
            form.Enabled = false;
            status.Clear();
            journal = LocalJournal();
            var results = await OnSta(() => RunPlan(path, plan, journal, progress));
            if (results.TryGetValue("ordem", out var number) && SelectedRoute == OrderRoute.ServiceOrderAndRequest)
                existingOrder.Text = number;
            WriteStatus("Fluxo SAP concluído.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            WriteStatus(error.Message);
        }
        finally
        {
            if (journal is not null) await PublishSheet(journal, progress);
            busy = false;
            form.Enabled = true;
        }
    }

    private static Dictionary<string, string> RunPlan(string profilePath, List<SapOperation> plan, OperationJournal journal, IProgress<string> progress)
    {
        var results = new Dictionary<string, string>();
        foreach (var entry in plan)
        {
            var operation = entry;
            if (entry.LinkFrom is { } source)
            {
                var values = new Dictionary<string, string>(entry.Values) { ["ordem"] = results[source] };
                operation = new SapOperation(entry.Name, values);
            }
            var known = journal.Read(operation);
            if (known is not null)
            {
                if (OperationJournal.IsPending(known))
                    throw new InvalidOperationException($"Existe uma tentativa sem resultado confirmado para {OperationLabel(operation.Name)}. Confira no SAP antes de tentar novamente.");
                results.Add(operation.Name, known);
                progress.Report($"{OperationLabel(operation.Name)} já registrada neste computador: {known}.");
                continue;
            }
            var began = false;
            try
            {
                SapAutomation.Run(profilePath, [operation], progress.Report,
                    onSaved: (name, number) =>
                    {
                        results.Add(name, number);
                        progress.Report($"{OperationLabel(name)} gravada: {number}.");
                        var fields = operation.Values;
                        var row = name == "ordem" ? new OrderSheetRow(number, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            fields["frota"], fields["empresa"], fields["statusPlanilha"], fields["tipoPlanilha"], fields["titulo"]) : null;
                        journal.Record(operation, number, row);
                    },
                    beforeSave: (_, _) =>
                    {
                        journal.Begin(operation);
                        began = true;
                    });
            }
            catch (SapRunException error) when (began && !error.SaveAttempted)
            {
                journal.CancelBeforeSave(operation);
                throw;
            }
        }
        return results;
    }

    private async Task SyncSheet(bool requested = false)
    {
        if (busy) return;
        busy = true;
        form.Enabled = false;
        try { await PublishSheet(LocalJournal(), new Progress<string>(WriteStatus), requested); }
        finally
        {
            busy = false;
            form.Enabled = true;
        }
    }

    private static OperationJournal LocalJournal() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssistenteSap", "historico"), DateOnly.FromDateTime(DateTime.Today));

    private async Task<bool> PublishSheet(OperationJournal journal, IProgress<string> progress, bool requested = false)
    {
        try { return await SheetSync.PublishPendingAsync(journal, progress, requested); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            WriteStatus(error is InvalidDataException ? $"Planilha: {error.Message}" : "Planilha: confira planilha.local.json e o histórico local. As ordens não foram reenviadas ao SAP.");
            return false;
        }
    }

    private static string OperationLabel(string name) => name == "ordem" ? "Ordem" : "Requisição";

    private static Task<T> OnSta<T>(Func<T> run)
    {
        var task = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { task.SetResult(run()); }
            catch (Exception error) { task.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return task.Task;
    }

    private void WriteStatus(string message)
    {
        status.AppendText(message + Environment.NewLine);
        status.ScrollToCaret();
    }

    private void AddField(string label, Control control, bool stretch = true)
    {
        var row = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 8, 3), Anchor = AnchorStyles.Left }, 0, row);
        if (stretch) control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        form.Controls.Add(control, 1, row);
    }

    private void AddFull(Control control)
    {
        var row = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(control, 0, row);
        form.SetColumnSpan(control, 2);
    }

    private static bool PositiveNumber(string value) => decimal.TryParse(value, NumberStyles.AllowDecimalPoint, Brazilian, out var number) && number > 0;

    private static bool SheetText(string value, int limit) => value.Trim() is { Length: > 0 } trimmed && trimmed.Length <= limit && !trimmed.Any(char.IsControl);

    internal void TestClassification()
    {
        foreach (var (name, expected) in new (string, OrderRoute)[]
        {
            ("SERV. BORRACHARIA", OrderRoute.ServiceOrderAndRequest),
            ("PEDIDO DE PEÇA PARA RETROVISOR", OrderRoute.PartRequest),
            ("APENAS ORDEM DE SERVIÇO", OrderRoute.ServiceOrder),
            ("FREIO", OrderRoute.Manual)
        })
        {
            title.Text = name;
            ApplySuggestion();
            if (SelectedRoute != expected)
                throw new InvalidOperationException($"Resultado inesperado na interface para '{name}'.");
        }
    }

    private sealed record RouteOption(OrderRoute Value, string Name)
    {
        public override string ToString() => Name;
    }
}
