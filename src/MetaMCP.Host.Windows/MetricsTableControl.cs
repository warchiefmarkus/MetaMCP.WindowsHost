namespace MetaMCP.Host;

/// <summary>
/// Compact metric cells hosted in the WinForms tray menu.
/// A real three-column layout keeps values aligned without pipe separators.
/// </summary>
internal sealed class MetricsTableControl : UserControl
{
    private readonly TableLayoutPanel _table;
    private readonly Label _mcp = CreateCell();
    private readonly Label _cpu = CreateCell();
    private readonly Label _ram = CreateCell();

    public MetricsTableControl()
    {
        AutoSize = false;
        Size = new Size(252, 24);
        MinimumSize = new Size(210, 24);
        Margin = Padding.Empty;
        Padding = Padding.Empty;

        _table = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        _table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _table.Controls.Add(_mcp, 0, 0);
        _table.Controls.Add(_cpu, 1, 0);
        _table.Controls.Add(_ram, 2, 0);
        Controls.Add(_table);
        SetValues(0, null);
        ApplySystemColors();
    }

    private static Label CreateCell() => new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
        AutoEllipsis = true,
    };

    public void SetValues(int connections, McpProcessMetrics? metrics)
    {
        _mcp.Text = $"MCP: {connections}";
        _cpu.Text = metrics is null ? "CPU: --" : $"CPU: {metrics.CpuPercent:0.0}%";
        _ram.Text = metrics is null ? "RAM: --" : $"RAM: {FormatMemory(metrics.WorkingSetBytes)}";
    }

    private static string FormatMemory(long bytes)
    {
        const double mb = 1024d * 1024d;
        const double gb = mb * 1024d;
        return bytes >= gb ? $"{bytes / gb:0.0} GB" : $"{bytes / mb:0} MB";
    }

    public void ApplySystemColors()
    {
        BackColor = SystemColors.Menu;
        ForeColor = SystemColors.MenuText;
        _table.BackColor = BackColor;
        foreach (var label in new[] { _mcp, _cpu, _ram })
        {
            label.BackColor = BackColor;
            label.ForeColor = ForeColor;
        }
    }
}
