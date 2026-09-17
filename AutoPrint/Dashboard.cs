using System.Net.Http.Json;
using System.Text.Json;

sealed class Dashboard : Form
{
    private static readonly Color Ink = Color.FromArgb(24, 39, 62);
    private static readonly Color Muted = Color.FromArgb(94, 110, 132);
    private static readonly Color Blue = Color.FromArgb(36, 99, 235);
    private readonly HttpClient client;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly ComboBox printers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox simulation = new() { Text = "Simular sem gastar papel", AutoSize = true, Checked = true };
    private readonly CheckBox paused = new() { Text = "Pausar o processamento da fila", AutoSize = true };
    private readonly Label connection = TextLabel("Conectando…", 10, true);
    private readonly Label active = TextLabel("Aguardando o AutoPrint", 10);
    private readonly Label feedback = TextLabel("", 10);
    private readonly Label modeValue = TextLabel("—", 20, true);
    private readonly Label queueValue = TextLabel("—", 20, true);
    private readonly Label doneValue = TextLabel("—", 20, true);
    private readonly Label attentionValue = TextLabel("—", 20, true);
    private readonly Label historyHint = TextLabel("Nenhum pedido recebido ainda.", 9);
    private readonly Button save = ActionButton("Salvar configurações", true);
    private readonly Button send = ActionButton("Enviar teste", true);
    private readonly TextBox sample = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = "TESTE AUTOPRINT\r\nConexão funcionando.\r\nTeste de impressão de texto.", AccessibleName = "Texto do teste" };
    private readonly DataGridView history = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        EnableHeadersVisualStyles = false, ColumnHeadersHeight = 38, RowTemplate = { Height = 34 },
        AccessibleName = "Histórico de pedidos"
    };
    private PrinterOptions? applied;
    private bool dirty, loading, refreshing, saving, sending;
    private string lastHistory = "";

    public Dashboard(string address, string apiKey)
    {
        Text = "AutoPrint • Painel de controle";
        ClientSize = new Size(1180, 790);
        MinimumSize = new Size(1060, 810);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        ForeColor = Ink;
        BackColor = Color.FromArgb(242, 245, 250);
        client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("X-AutoPrint-Key", apiKey);

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 186));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Ink, Padding = new Padding(20, 30, 16, 22), Margin = Padding.Empty };
        var brand = TextLabel("AutoPrint", 24, true); brand.ForeColor = Color.White; brand.Dock = DockStyle.Top; brand.Height = 56;
        var menu = TextLabel("SEU FLUXO\n\n01  Configure\n      a impressora\n\n02  Conecte\n      seu sistema\n\n03  Acompanhe\n      os pedidos", 10);
        menu.ForeColor = Color.FromArgb(197, 211, 233); menu.Dock = DockStyle.Fill;
        var foot = TextLabel("WINDOWS\n\nFechar este painel\nencerra o AutoPrint.", 9); foot.ForeColor = Color.FromArgb(177, 196, 219); foot.Dock = DockStyle.Bottom; foot.Height = 95;
        sidebar.Controls.Add(menu); sidebar.Controls.Add(foot); sidebar.Controls.Add(brand);
        shell.Controls.Add(sidebar, 0, 0);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        shell.Controls.Add(main, 1, 0); Controls.Add(shell);

        var heading = new Panel { Dock = DockStyle.Fill };
        var title = TextLabel("Central de impressão", 23, true); title.Dock = DockStyle.None; title.SetBounds(0, 0, 570, 39);
        var subtitle = TextLabel("Configure uma vez. Acompanhe cada pedido por aqui.", 10); subtitle.Dock = DockStyle.None; subtitle.SetBounds(2, 44, 620, 24);
        connection.Dock = DockStyle.Right; connection.Width = 180; connection.TextAlign = ContentAlignment.TopRight;
        heading.Controls.Add(title); heading.Controls.Add(subtitle); heading.Controls.Add(connection); main.Controls.Add(heading, 0, 0);
        var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        foreach (var (label, value) in new[] { ("MODO ATIVO", modeValue), ("NA FILA", queueValue), ("PROCESSADOS", doneValue), ("CONFERIR", attentionValue) })
        {
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0, 0, 10, 14), Padding = new Padding(14, 10, 10, 8) };
            var caption = TextLabel(label, 8, true); caption.Dock = DockStyle.Top; caption.Height = 22;
            value.Dock = DockStyle.Fill; card.Controls.Add(value); card.Controls.Add(caption); stats.Controls.Add(card);
        }
        main.Controls.Add(stats, 0, 1);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 326)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.Controls.Add(body, 0, 2);

        var configuration = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18), ColumnCount = 1, RowCount = 11, Margin = new Padding(0, 0, 14, 12) };
        foreach (var height in new[] { 26, 22, 32, 36, 28, 28, 42, 44, 46, 34 }) configuration.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        configuration.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        configuration.Controls.Add(TextLabel("Sua impressora", 15, true), 0, 0);
        configuration.Controls.Add(TextLabel("Impressora instalada no Windows", 9), 0, 1);
        printers.AccessibleName = "Impressora selecionada";
        configuration.Controls.Add(printers, 0, 2);
        var reload = ActionButton("Atualizar impressoras"); configuration.Controls.Add(reload, 0, 3);
        configuration.Controls.Add(simulation, 0, 4); configuration.Controls.Add(paused, 0, 5);
        configuration.Controls.Add(TextLabel("Desmarque a simulação para imprimir de verdade. A pausa mantém os pedidos na fila.", 9), 0, 6);
        configuration.Controls.Add(save, 0, 7);
        configuration.Controls.Add(active, 0, 8);
        var reset = ActionButton("Recarregar configuração"); configuration.Controls.Add(reset, 0, 9);
        configuration.Controls.Add(TextLabel("Alterações valem para os próximos pedidos. Um envio já iniciado termina com a configuração anterior.", 9), 0, 10);
        body.Controls.Add(configuration, 0, 0);

        var work = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18), ColumnCount = 1, RowCount = 7, Margin = new Padding(0, 0, 0, 12) };
        foreach (var row in new[] { new RowStyle(SizeType.Absolute, 32), new RowStyle(SizeType.Absolute, 28), new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.Absolute, 28), new RowStyle(SizeType.Absolute, 72), new RowStyle(SizeType.Absolute, 44), new RowStyle(SizeType.Absolute, 50) }) work.RowStyles.Add(row);
        work.Controls.Add(TextLabel("Fila e histórico", 15, true), 0, 0);
        work.Controls.Add(historyHint, 0, 1);
        history.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(242, 245, 250), ForeColor = Muted, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        history.DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Ink, BackColor = Color.White, SelectionBackColor = Color.FromArgb(227, 236, 255), SelectionForeColor = Ink, Font = new Font("Segoe UI", 9), Padding = new Padding(4) };
        history.Columns.Add("reference", "Pedido"); history.Columns.Add("status", "Situação"); history.Columns.Add("time", "Recebido");
        history.Columns[0].FillWeight = 42; history.Columns[1].FillWeight = 33; history.Columns[2].FillWeight = 25;
        work.Controls.Add(history, 0, 2); work.Controls.Add(TextLabel("TESTE DE CONEXÃO E IMPRESSÃO", 8, true), 0, 3);
        work.Controls.Add(sample, 0, 4); work.Controls.Add(send, 0, 5); work.Controls.Add(feedback, 0, 6);
        body.Controls.Add(work, 1, 0);

        var integration = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16, 10, 16, 10), ColumnCount = 3, RowCount = 3, Margin = Padding.Empty };
        integration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); integration.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); integration.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        integration.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); integration.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); integration.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        integration.Controls.Add(TextLabel("Conectar seu sistema", 12, true), 0, 0);
        var endpoint = new TextBox { ReadOnly = true, Text = address.TrimEnd('/') + "/api/jobs", Dock = DockStyle.Fill, AccessibleName = "Endereço para integração" };
        integration.Controls.Add(endpoint, 0, 1);
        var copyAddress = ActionButton("Copiar endereço"); var copyKey = ActionButton("Copiar chave");
        integration.Controls.Add(copyAddress, 1, 1); integration.Controls.Add(copyKey, 2, 1);
        var detail = TextLabel("Integração local por API • use o endereço e a chave no sistema que enviará os pedidos.", 9);
        integration.Controls.Add(detail, 0, 2); integration.SetColumnSpan(detail, 3); main.Controls.Add(integration, 0, 3);

        printers.SelectedIndexChanged += (_, _) => Changed(); simulation.CheckedChanged += (_, _) => Changed(); paused.CheckedChanged += (_, _) => Changed();
        reload.Click += async (_, _) => await LoadPrinters();
        reset.Click += async (_, _) => { dirty = false; await RefreshState(true); };
        save.Click += async (_, _) => await SaveSettings(); send.Click += async (_, _) => await SendTest();
        history.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && history.Rows[e.RowIndex].Tag is PrintJob job)
                MessageBox.Show(this, $"Pedido: {job.Reference}\nSituação: {StatusText(job.Status)}\nImpressora: {job.PrinterName ?? "—"}\nConfiguração: {job.SettingsRevision}\n\n{job.Error ?? job.Text}", "Detalhes do pedido", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        copyAddress.Click += (_, _) => Copy(endpoint.Text, "Endereço copiado."); copyKey.Click += (_, _) => Copy(apiKey, "Chave copiada. Cole apenas no sistema autorizado.");
        timer.Tick += async (_, _) => await RefreshState();
        Shown += async (_, _) => { await LoadPrinters(); await RefreshState(true); timer.Start(); };
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); client.Dispose(); };
    }

    private void Changed()
    {
        if (loading) return;
        dirty = true; active.Text = "Alterações ainda não salvas."; active.ForeColor = Color.DarkOrange;
    }
    private async Task LoadPrinters()
    {
        try
        {
            var list = await client.GetFromJsonAsync<string[]>("/api/printers") ?? [];
            if (IsDisposed) return;
            var selected = printers.SelectedItem as string ?? applied?.PrinterName;
            loading = true;
            try
            {
                printers.Items.Clear(); printers.Items.Add("Selecionar impressora…"); printers.Items.AddRange(list);
                if (!string.IsNullOrEmpty(selected) && !printers.Items.Contains(selected)) printers.Items.Add(selected);
                printers.SelectedItem = !string.IsNullOrEmpty(selected) ? selected : printers.Items[0];
            }
            finally { loading = false; }
            if (list.Length == 0) ShowFeedback("Nenhuma impressora instalada. Adicione uma no Windows.", true);
        }
        catch (Exception ex) { ShowFeedback("Não foi possível listar impressoras: " + ex.Message, true); }
    }
    private async Task RefreshState(bool force = false)
    {
        if (refreshing || saving || IsDisposed) return;
        refreshing = true;
        try
        {
            var options = await client.GetFromJsonAsync<PrinterOptions>("/api/settings") ?? throw new InvalidDataException("Configuração indisponível.");
            var jobs = await client.GetFromJsonAsync<PrintJob[]>("/api/jobs") ?? [];
            if (IsDisposed || saving) return;
            connection.Text = "● AutoPrint conectado"; connection.ForeColor = Color.FromArgb(20, 130, 95);
            save.Enabled = true; send.Enabled = !sending;
            if (force || (!dirty && applied?.Revision != options.Revision)) Apply(options);
            else if (dirty && applied?.Revision != options.Revision) { active.Text = "Configuração alterada externamente. Recarregue antes de salvar."; active.ForeColor = Color.DarkOrange; }
            modeValue.Text = options.Paused ? "Pausado" : options.Simulation ? "Simulação" : "Real";
            queueValue.Text = jobs.Count(j => j.Status is "pending" or "processing").ToString();
            doneValue.Text = jobs.Count(j => j.Status is "simulated" or "sent").ToString();
            attentionValue.Text = jobs.Count(j => j.Status == "uncertain").ToString();
            var signature = string.Join('|', jobs.Select(j => $"{j.Id}:{j.Status}"));
            if (signature != lastHistory)
            {
                lastHistory = signature;
                history.Rows.Clear();
                foreach (var job in jobs.Reverse().Take(100))
                {
                    var row = history.Rows[history.Rows.Add(job.Reference, StatusText(job.Status), job.CreatedAt.ToLocalTime().ToString("dd/MM HH:mm"))];
                    row.Tag = job; row.Cells[1].ToolTipText = job.Error ?? "";
                    if (job.Status == "uncertain") row.DefaultCellStyle.ForeColor = Color.Firebrick;
                }
            }
            historyHint.Text = jobs.Length == 0 ? "Nenhum pedido recebido ainda." : $"{jobs.Length} pedido(s) • últimos 100 • duplo clique para detalhes";
        }
        catch (Exception ex)
        {
            if (!IsDisposed) { connection.Text = "● Sem conexão"; connection.ForeColor = Color.Firebrick; save.Enabled = false; send.Enabled = false; ShowFeedback("Tentando reconectar ao AutoPrint. " + ex.Message, true); }
        }
        finally { refreshing = false; }
    }
    private void Apply(PrinterOptions options)
    {
        loading = true;
        try
        {
            if (!string.IsNullOrEmpty(options.PrinterName) && !printers.Items.Contains(options.PrinterName)) printers.Items.Add(options.PrinterName);
            if (printers.Items.Count > 0) printers.SelectedItem = string.IsNullOrEmpty(options.PrinterName) ? printers.Items[0] : options.PrinterName;
            simulation.Checked = options.Simulation; paused.Checked = options.Paused;
            applied = options; dirty = false;
            active.Text = $"Reconhecida pelo AutoPrint • versão {options.Revision}\n{(string.IsNullOrEmpty(options.PrinterName) ? "Nenhuma impressora selecionada" : options.PrinterName)}";
            active.ForeColor = Color.FromArgb(20, 130, 95);
        }
        finally { loading = false; }
    }
    private async Task SaveSettings()
    {
        if (saving || applied is null) return;
        saving = true; save.Enabled = false;
        try
        {
            var request = new SettingsRequest(printers.SelectedIndex <= 0 ? "" : printers.SelectedItem?.ToString(), simulation.Checked, paused.Checked, applied.Revision);
            using var response = await client.PutAsJsonAsync("/api/settings", request);
            await EnsureSuccess(response);
            var result = await response.Content.ReadFromJsonAsync<PrinterOptions>() ?? throw new InvalidDataException("Resposta inválida.");
            if (IsDisposed) return;
            Apply(result); ShowFeedback("Configuração salva e reconhecida pelo AutoPrint.");
        }
        catch (Exception ex) { ShowFeedback(ex.Message, true); }
        finally { saving = false; if (!IsDisposed) save.Enabled = true; }
        await RefreshState();
    }
    private async Task SendTest()
    {
        if (sending) return;
        if (dirty) { ShowFeedback("Salve ou recarregue as configurações antes de testar.", true); return; }
        if (applied is null) return;
        if (!applied.Simulation && MessageBox.Show(this, $"Enviar este texto para {applied.PrinterName}?", "Impressão real", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        sending = true; send.Enabled = false;
        try
        {
            using var response = await client.PostAsJsonAsync("/api/jobs", new SubmitJob("teste-" + Guid.NewGuid().ToString("N")[..10], sample.Text));
            await EnsureSuccess(response);
            ShowFeedback(applied.Paused ? "Teste recebido. Aguardando liberar a fila." : "Teste recebido. Acompanhe o resultado no histórico.");
            await RefreshState();
        }
        catch (Exception ex) { ShowFeedback(ex.Message, true); }
        finally { sending = false; if (!IsDisposed) send.Enabled = true; }
    }
    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync();
        try { using var json = JsonDocument.Parse(text); if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString()); }
        catch (JsonException) { }
        throw new InvalidOperationException($"AutoPrint respondeu com erro {(int)response.StatusCode}.");
    }
    private void Copy(string text, string message)
    {
        try { Clipboard.SetText(text); ShowFeedback(message); }
        catch (Exception) { ShowFeedback("Não foi possível copiar agora. Tente novamente.", true); }
    }
    private void ShowFeedback(string message, bool error = false)
    {
        if (IsDisposed) return;
        feedback.Text = message; feedback.ForeColor = error ? Color.Firebrick : Color.FromArgb(20, 130, 95);
    }
    private static string StatusText(string status) => status switch
    {
        "pending" => "Na fila", "processing" => "Enviando", "simulated" => "Simulado",
        "sent" => "Enviado ao Windows", "uncertain" => "Conferir envio", _ => status
    };
    private static Label TextLabel(string text, float size, bool bold = false) => new()
    {
        Text = text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = bold ? Ink : Muted, Dock = DockStyle.Fill, AutoEllipsis = true, Margin = Padding.Empty
    };
    private static Button ActionButton(string text, bool primary = false) => new()
    {
        Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderSize = 0 }, BackColor = primary ? Blue : Color.FromArgb(237, 242, 250),
        ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand,
        Font = new Font("Segoe UI", 9, primary ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 3, 4, 5)
    };
}
