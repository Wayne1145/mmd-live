namespace MmdLive;

// Shared look for the control window. Light surfaces, one teal accent, grey secondary text.
internal static class Theme
{
    public static readonly Color Surface = Color.FromArgb(244, 246, 245);
    public static readonly Color Panel = Color.White;
    public static readonly Color Ink = Color.FromArgb(27, 44, 46);
    public static readonly Color Muted = Color.FromArgb(104, 118, 120);
    public static readonly Color Accent = Color.FromArgb(0, 118, 108);
    public static readonly Color AccentSoft = Color.FromArgb(220, 240, 237);
    public static readonly Color Border = Color.FromArgb(214, 221, 220);
    public static readonly Color Danger = Color.FromArgb(176, 52, 44);
    public static readonly Color Off = Color.FromArgb(178, 188, 189);
    public static readonly Color Warning = Color.FromArgb(171, 102, 0);
}

// A row of mutually exclusive buttons with a ComboBox-like SelectedIndex.
internal sealed class Segmented : TableLayoutPanel
{
    readonly List<RadioButton> items = [];
    public event EventHandler? SelectedIndexChanged;

    public Segmented(params string[] labels)
    {
        ColumnCount = labels.Length;
        RowCount = 1;
        Height = 32;
        Margin = new Padding(0, 0, 0, 8);
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var _ in labels)
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / labels.Length));
        for (int i = 0; i < labels.Length; i++)
        {
            var item = new RadioButton
            {
                Text = labels[i],
                Appearance = Appearance.Button,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Margin = new Padding(i == 0 ? 0 : 2, 0, 0, 0),
                BackColor = Theme.Panel,
                ForeColor = Theme.Ink,
                Cursor = Cursors.Hand,
            };
            item.FlatAppearance.BorderColor = Theme.Border;
            item.FlatAppearance.CheckedBackColor = Theme.Accent;
            item.CheckedChanged += (_, _) =>
            {
                item.ForeColor = item.Checked ? Color.White : Theme.Ink;
                if (item.Checked)
                    SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            };
            items.Add(item);
            Controls.Add(item, i, 0);
        }
        items[0].Checked = true;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => items.FindIndex(r => r.Checked);
        set => items[Math.Clamp(value, 0, items.Count - 1)].Checked = true;
    }
}

// A slider with its label on the left and the live value on the right.
internal sealed class Slider : TableLayoutPanel
{
    readonly Label value = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Right,
        ForeColor = Theme.Muted,
        TextAlign = ContentAlignment.MiddleRight,
    };
    readonly string unit;
    public TrackBar Bar { get; }

    public Slider(string label, int min, int max, string unit)
    {
        this.unit = unit;
        ColumnCount = 3;
        RowCount = 1;
        Height = 36;
        Margin = new Padding(0, 0, 0, 2);
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        Bar = new TrackBar
        {
            Minimum = min,
            Maximum = max,
            TickStyle = TickStyle.None,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            AutoSize = false,
            Height = 30,
            LargeChange = Math.Max(1, (max - min) / 10),
            SmallChange = 1,
            BackColor = Theme.Panel,
            AccessibleName = label,
        };
        Bar.ValueChanged += (_, _) => value.Text = Bar.Value + unit;
        Controls.Add(
            new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
            },
            0,
            0
        );
        Controls.Add(Bar, 1, 0);
        Controls.Add(value, 2, 0);
        value.Text = Bar.Value + unit;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => Bar.Value;
        set => Bar.Value = Math.Clamp(value, Bar.Minimum, Bar.Maximum);
    }
}

// One tracked part in the status bar: a dot that lights up while the part is seen.
internal sealed class StatusChip : Label
{
    readonly string name;
    bool on;

    public StatusChip(string name)
    {
        this.name = name;
        AutoSize = true;
        Margin = new Padding(0, 6, 14, 0);
        Set(false);
    }

    public void Set(bool seen)
    {
        if (seen == on && Text.Length > 0)
            return;
        on = seen;
        Text = "● " + name;
        ForeColor = seen ? Theme.Accent : Theme.Off;
    }
}
