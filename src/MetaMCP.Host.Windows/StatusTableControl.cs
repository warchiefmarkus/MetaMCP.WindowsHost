namespace MetaMCP.Host;

internal sealed class StatusTableControl : UserControl
{
    private const int RowCount = 5;

    private readonly TableLayoutPanel _table;
    private readonly PictureBox[] _indicators = new PictureBox[RowCount];
    private readonly Label[] _labels = new Label[RowCount];

    public StatusTableControl()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        Padding = new Padding(0, 2, 0, 2);

        _table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = RowCount,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        for (var row = 0; row < RowCount; row++)
        {
            _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var indicator = new PictureBox
            {
                Size = new Size(14, 14),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Anchor = AnchorStyles.Left,
                BackColor = Color.Transparent,
                Margin = new Padding(1, 3, 5, 3),
            };
            var label = new Label
            {
                AutoSize = true,
                MinimumSize = new Size(0, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 1, 12, 1),
            };

            _indicators[row] = indicator;
            _labels[row] = label;
            _table.Controls.Add(indicator, 0, row);
            _table.Controls.Add(label, 1, row);
        }

        Controls.Add(_table);
        ApplySystemColors();
    }

    public void SetRow(int row, string text, Image? indicator)
    {
        if ((uint)row >= RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        _labels[row].Text = text;
        _indicators[row].Image = indicator;
    }

    public void ApplySystemColors()
    {
        BackColor = SystemColors.Menu;
        ForeColor = SystemColors.MenuText;
        _table.BackColor = BackColor;
        foreach (var label in _labels)
        {
            label.ForeColor = ForeColor;
        }
    }
}
