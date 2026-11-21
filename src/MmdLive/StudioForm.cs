using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MmdLive.Rendering;
using MmdLive.Rig;
using MmdLive.Tracking;

namespace MmdLive;

internal sealed class StudioProfile
{
    public StudioLights Lights { get; set; } = new();
    public bool FullBody { get; set; }
    public bool ShowBones { get; set; }
    public float Zoom { get; set; } = 1;
    public Dictionary<string, string> Bones { get; set; } = new();
    public Dictionary<string, string> Morphs { get; set; } = new();
    public float[] Neutral { get; set; } = [];
}

internal sealed class OutputWindow : Form
{
    public OutputWindow()
    {
        Text = "MMD Live — OBS Output";
        BackColor = Color.Black;
        ClientSize = new Size(768, 432);
        MinimumSize = new Size(300, 190);
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
}

internal sealed class StudioForm : Form
{
    readonly OutputWindow output = new();
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly TabPage previewPage = new("追踪预览") { BackColor = Color.White };
    readonly TrackingPreview preview = new();
    readonly CheckBox showBones = new() { Text = "显示骨骼", AutoSize = true };
    readonly TextBox boneNames = new()
    {
        Dock = DockStyle.Bottom,
        Height = 46,
        Multiline = true,
        ReadOnly = true,
        Visible = false,
        ScrollBars = ScrollBars.Vertical,
        AccessibleName = "本帧驱动骨名",
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
    };
    string displayedNames = "";
    long lastPreview;
    bool previewDirty = true;
    readonly ComboBox adapter = Choice(),
        camera = Choice(),
        mode = Choice(),
        lightSelector = Choice();
    readonly NumericUpDown azimuth = Number(-180, 180),
        elevation = Number(-90, 90),
        power = Number(0, 250),
        zoom = Number(50, 200);
    readonly Button color = ButtonFor("选择颜色");
    readonly Label modelName = ValueLabel(),
        trackingState = ValueLabel(),
        trackingDetails = ValueLabel();
    readonly PlaybackMetrics? metrics;
    bool rendering;
    long nextFrame;
    readonly SemaphoreSlim switching = new(1, 1);
    readonly string settingsPath;
    Renderer? renderer;
    MotionInput? input;
    StudioProfile profile = new();
    string modelFile = Program.Demo,
        profileFile = "";
    bool loading,
        closing;
    long lastStatus;

    public StudioForm(string[] launch)
    {
        string? Arg(string name)
        {
            int n = Array.IndexOf(launch, name);
            return n >= 0 && n + 1 < launch.Length ? launch[n + 1] : null;
        }
        if (Arg("--metrics") is { } metricsDir)
            metrics = new PlaybackMetrics(
                metricsDir,
                int.TryParse(Arg("--seconds"), out int seconds)
                    ? Math.Clamp(seconds, 5, 86400)
                    : 1800
            );
        settingsPath = Arg("--settings-dir")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MmdLive",
                "profiles"
            );
        Directory.CreateDirectory(settingsPath);
        Text = "MMD Live · 控制台";
        ClientSize = new Size(860, 640);
        MinimumSize = new Size(540, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(245, 246, 243);
        ForeColor = Color.FromArgb(27, 44, 46);
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 16, 22, 12),
            ColumnCount = 1,
            RowCount = 4,
        };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(main);
        var title = new Label
        {
            Text = "MMD Live",
            Dock = DockStyle.Fill,
            Font = new Font("Inter", 22, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        main.Controls.Add(title, 0, 0);
        main.Controls.Add(tabs, 0, 1);
        var session = Sheet(tabs, "直播控制");
        tabs.TabPages.Add(previewPage);
        var lights = Sheet(tabs, "虚拟灯光");
        var previewBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 39,
            Padding = new Padding(8, 5, 3, 2),
            WrapContents = false,
        };
        previewBar.Controls.Add(showBones);
        foreach (
            var (text, ink) in new[]
            {
                ("青色 · 真人追踪", Color.FromArgb(0, 104, 96)),
                ("橙色 · 模型驱动", Color.FromArgb(145, 77, 2)),
            }
        )
            previewBar.Controls.Add(
                new Label
                {
                    Text = text,
                    ForeColor = ink,
                    AutoSize = true,
                    Margin = new Padding(9, 4, 0, 0),
                }
            );
        var stopPreview = ButtonFor("停止输入");
        stopPreview.Margin = new Padding(10, 0, 0, 0);
        stopPreview.Click += (_, _) => StopInput();
        previewBar.Controls.Add(stopPreview);
        previewPage.Controls.Add(preview);
        previewPage.Controls.Add(boneNames);
        previewPage.Controls.Add(previewBar);
        showBones.CheckedChanged += (_, _) =>
        {
            preview.ShowBones = showBones.Checked;
            boneNames.Visible = showBones.Checked;
            SetBoneNames("");
            previewDirty = true;
            if (!loading)
                profile.ShowBones = showBones.Checked;
        };
        tabs.SelectedIndexChanged += (_, _) => UpdatePreviewVisibility();
        Resize += (_, _) => UpdatePreviewVisibility();
        void Row(TableLayoutPanel panel, string name, Control value)
        {
            int r = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            panel.Controls.Add(
                new Label
                {
                    Text = name,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                },
                0,
                r
            );
            value.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            panel.Controls.Add(value, 1, r);
        }
        Row(session, "图形设备", adapter);
        Row(session, "当前角色", modelName);
        Row(session, "角色", ActionRow(("打开模型…", OpenModel), ("绑定设置…", EditMapping)));
        Row(
            session,
            "文件输入",
            ActionRow(("导入图片…", () => OpenSource(false)), ("播放视频…", () => OpenSource(true)))
        );
        camera.Items.Add("先读取设备列表");
        camera.SelectedIndex = 0;
        Row(session, "摄像头", camera);
        Row(
            session,
            "设备控制",
            ActionRow(
                ("读取设备列表", RefreshCameras),
                ("开始摄像头", StartCamera),
                ("停止输入", StopInput)
            )
        );
        mode.Items.AddRange(["半身", "全身"]);
        mode.SelectedIndex = 0;
        Row(session, "画面构图", mode);
        zoom.Value = 100;
        Row(session, "缩放 (%)", zoom);
        Row(session, "追踪", ActionRow(("校准中性姿势", Calibrate), ("显示输出窗口", () =>
                {
                    output.Show();
                    output.Activate();
                })));
        lightSelector.Items.AddRange(["主光", "补光", "轮廓光"]);
        lightSelector.SelectedIndex = 0;
        Row(lights, "灯光", lightSelector);
        Row(lights, "水平方位 (°)", azimuth);
        Row(lights, "仰角 (°)", elevation);
        Row(lights, "强度 (%)", power);
        Row(lights, "光色", color);
        var reset = ButtonFor("恢复默认灯光");
        reset.Click += (_, _) =>
        {
            profile.Lights = new();
            RefreshLight();
            ApplyProfile();
        };
        Row(lights, "预设", reset);
        trackingState.Dock = trackingDetails.Dock = DockStyle.Fill;
        main.Controls.Add(trackingState, 0, 2);
        main.Controls.Add(trackingDetails, 0, 3);
        trackingDetails.ForeColor = Color.FromArgb(60, 95, 89);
        var gpus = GraphicsAdapter.List();
        if (gpus.Count == 0)
            throw new InvalidOperationException("未找到 Direct3D 硬件显卡");
        foreach (var gpu in gpus)
            adapter.Items.Add(gpu);
        adapter.SelectedIndex = 0;
        mode.SelectedIndexChanged += (_, _) => ApplyProfile();
        zoom.ValueChanged += (_, _) => ApplyProfile();
        lightSelector.SelectedIndexChanged += (_, _) => RefreshLight();
        azimuth.ValueChanged += (_, _) => ChangeLight();
        elevation.ValueChanged += (_, _) => ChangeLight();
        power.ValueChanged += (_, _) => ChangeLight();
        color.Click += (_, _) =>
        {
            using var picker = new ColorDialog
            {
                Color = Color.FromArgb(CurrentLight().Rgb | unchecked((int)0xff000000)),
                FullOpen = true,
            };
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                SetLight(CurrentLight() with { Rgb = picker.Color.ToArgb() & 0xffffff });
                RefreshLight();
                ApplyProfile();
            }
        };
        adapter.SelectedIndexChanged += async (_, _) => await WithSwitch(RebuildRenderer);
        output.FormClosing += (_, e) =>
        {
            if (!closing)
            {
                e.Cancel = true;
                output.Hide();
            }
        };
        Application.Idle += OnIdle;
        Shown += async (_, _) =>
            await WithSwitch(async () =>
            {
                var area = Screen.FromControl(this).WorkingArea;
                output.Location = new Point(
                    Math.Max(area.Left, area.Right - output.Width - 22),
                    area.Top + 42
                );
                output.Show();
                modelFile = Arg("--model") ?? Program.Demo;
                await RebuildRenderer();
                if (Arg("--image") is { } photo)
                    await StartInput(new(SourceKind.Image, photo));
                if (Arg("--video") is { } movie)
                    await StartInput(new(SourceKind.Video, movie));
                if (launch.Contains("--full"))
                    mode.SelectedIndex = 1;
            });
        FormClosing += (_, _) =>
        {
            closing = true;
            rendering = false;
            Application.Idle -= OnIdle;
            Save();
            input?.Dispose();
            renderer?.Dispose();
            output.Close();
        };
    }

    static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };

    static NumericUpDown Number(int min, int max) =>
        new()
        {
            Minimum = min,
            Maximum = max,
            Width = 120,
            TextAlign = HorizontalAlignment.Right,
        };

    static Label ValueLabel() =>
        new()
        {
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Height = 25,
        };

    static Button ButtonFor(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Height = 30,
            Padding = new Padding(4, 0, 4, 0),
        };

    static Control ActionRow(params (string label, Action action)[] actions)
    {
        var row = new FlowLayoutPanel
        {
            Height = 38,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        foreach (var (label, action) in actions)
        {
            var b = ButtonFor(label);
            b.Click += (_, _) => action();
            row.Controls.Add(b);
        }
        return row;
    }

    static TableLayoutPanel Sheet(TabControl tabs, string name)
    {
        var page = new TabPage(name) { BackColor = Color.White };
        tabs.TabPages.Add(page);
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 0,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(table);
        return table;
    }

    async Task WithSwitch(Func<Task> action)
    {
        if (!await switching.WaitAsync(0))
            return;
        try
        {
            await action();
        }
        catch (Exception e)
        {
            trackingState.Text = e.GetBaseException().Message;
            MessageBox.Show(
                this,
                e.Message,
                "MMD Live",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
        finally
        {
            switching.Release();
        }
    }

    async Task RebuildRenderer()
    {
        rendering = false;
        Save();
        if (input != null)
        {
            await input.Stop();
            input.Dispose();
        }
        renderer?.Dispose();
        var gpu = (GraphicsAdapter)adapter.SelectedItem!;
        renderer = new Renderer(output.Handle, gpu.Index, 1920, 1080);
        preview.ClearFrame();
        input = new MotionInput(gpu.Index);
        UpdatePreviewVisibility();
        LoadModel(modelFile);
        rendering = true;
    }

    void LoadModel(string path)
    {
        var rig = new AvatarRig(path);
        string hash;
        using (var f = File.OpenRead(path))
            hash = Convert.ToHexString(SHA256.HashData(f));
        var storage = Path.Combine(settingsPath, hash + ".json");
        var preset = File.Exists(storage)
            ? JsonSerializer.Deserialize<StudioProfile>(File.ReadAllText(storage)) ?? new()
            : new StudioProfile();
        rig.Bind(preset.Bones, preset.Morphs);
        rig.Neutral = preset.Neutral;
        renderer!.Load(rig);
        modelFile = path;
        profileFile = storage;
        profile = preset;
        loading = true;
        mode.SelectedIndex = profile.FullBody ? 1 : 0;
        showBones.Checked = profile.ShowBones;
        previewDirty = true;
        zoom.Value = Math.Clamp((decimal)profile.Zoom * 100, 50, 200);
        loading = false;
        RefreshLight();
        ApplyProfile();
        modelName.Text =
            $"{rig.Model.Name}   ·   {rig.Model.Bones.Count} 骨骼   ·   {rig.Model.Morphs.Count} 表情";
    }

    void OpenModel()
    {
        using var d = new OpenFileDialog { Filter = "MMD 模型 (*.pmx, *.pmd)|*.pmx;*.pmd" };
        if (d.ShowDialog(this) == DialogResult.OK)
            _ = WithSwitch(() =>
            {
                Save();
                LoadModel(d.FileName);
                return Task.CompletedTask;
            });
    }

    void OpenSource(bool video)
    {
        using var d = new OpenFileDialog
        {
            Filter = video
                ? "视频|*.mp4;*.mkv;*.avi;*.mov;*.webm"
                : "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
        };
        if (d.ShowDialog(this) == DialogResult.OK && input != null)
            _ = WithSwitch(() =>
                StartInput(new(video ? SourceKind.Video : SourceKind.Image, d.FileName))
            );
    }

    void UpdatePreviewVisibility()
    {
        if (input != null)
            input.PreviewVisible =
                tabs.SelectedTab == previewPage && WindowState != FormWindowState.Minimized;
        previewDirty = true;
    }

    async Task StartInput(Source source)
    {
        if (input == null)
            return;
        tabs.SelectedTab = previewPage;
        preview.ClearFrame();
        UpdatePreviewVisibility();
        await input.Start(source);
    }

    void StopInput() =>
        _ = WithSwitch(async () =>
        {
            if (input != null)
                await input.Stop();
            preview.ClearFrame();
        });

    void RefreshCameras()
    {
        try
        {
            var list = CameraDevices.Names();
            camera.Items.Clear();
            camera.Items.AddRange(list);
            if (list.Length > 0)
                camera.SelectedIndex = 0;
            else
                trackingState.Text = "没有可用摄像头";
        }
        catch (Exception e)
        {
            trackingState.Text = e.Message;
        }
    }

    void StartCamera()
    {
        if (camera.SelectedIndex < 0 || camera.SelectedItem?.ToString() == "先读取设备列表")
            RefreshCameras();
        if (camera.SelectedIndex >= 0 && input != null)
            _ = WithSwitch(() => StartInput(new(SourceKind.Camera, "", camera.SelectedIndex)));
    }

    void Calibrate()
    {
        if (renderer?.Rig is not { } rig || input == null)
            return;
        rig.ResetPoseOrigin();
        if (input.Latest.Expressions.Length != 52)
            return;
        renderer.Rig.Calibrate(input.Latest);
        profile.Neutral = renderer.Rig.Neutral;
        Save();
    }

    void EditMapping()
    {
        if (renderer?.Rig is not { } rig)
            return;
        using var dialog = new MappingDialog(rig, profile);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            profile.Bones = dialog.Bones;
            profile.Morphs = dialog.Morphs;
            rig.Bind(profile.Bones, profile.Morphs);
            Save();
        }
    }

    Light CurrentLight() =>
        lightSelector.SelectedIndex switch
        {
            1 => profile.Lights.Fill,
            2 => profile.Lights.Rim,
            _ => profile.Lights.Key,
        };

    void SetLight(Light light)
    {
        switch (lightSelector.SelectedIndex)
        {
            case 1:
                profile.Lights.Fill = light;
                break;
            case 2:
                profile.Lights.Rim = light;
                break;
            default:
                profile.Lights.Key = light;
                break;
        }
    }

    void RefreshLight()
    {
        loading = true;
        var l = CurrentLight();
        azimuth.Value = (decimal)l.Azimuth;
        elevation.Value = (decimal)l.Elevation;
        power.Value = (decimal)Math.Round(l.Power * 100);
        color.BackColor = Color.FromArgb(l.Rgb | unchecked((int)0xff000000));
        color.ForeColor = color.BackColor.GetBrightness() > .5 ? Color.Black : Color.White;
        loading = false;
    }

    void ChangeLight()
    {
        if (loading)
            return;
        SetLight(
            new(
                (float)azimuth.Value,
                (float)elevation.Value,
                (float)power.Value / 100,
                CurrentLight().Rgb
            )
        );
        ApplyProfile();
    }

    void ApplyProfile()
    {
        if (loading || renderer is null)
            return;
        previewDirty = true;
        renderer.Lights = profile.Lights;
        renderer.FullBody = profile.FullBody = mode.SelectedIndex == 1;
        renderer.Zoom = profile.Zoom = (float)zoom.Value / 100;
    }

    void Save()
    {
        if (profileFile.Length == 0)
            return;
        string temp = profileFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, Program.Json));
        File.Move(temp, profileFile, true);
    }

    void SetBoneNames(string names)
    {
        if (names == displayedNames)
            return;
        displayedNames = names;
        boneNames.Text = names;
    }

    void OnIdle(object? sender, EventArgs e)
    {
        while (rendering && !closing && !RenderClock.HasMessages())
        {
            long now = Stopwatch.GetTimestamp();
            double wait = (nextFrame - now) * 1000.0 / Stopwatch.Frequency;
            if (wait > 1)
            {
                RenderClock.Wait((int)wait);
                continue;
            }
            if (now < nextFrame)
            {
                Thread.Yield();
                continue;
            }
            nextFrame =
                Math.Max(now - Stopwatch.Frequency / 60, nextFrame) + Stopwatch.Frequency / 60;
            Frame();
        }
    }

    void Frame()
    {
        if (renderer == null || input == null)
            return;
        var sample = input.Read(out var bitmap);
        var m = sample.Detected;
        long now = Environment.TickCount64;
        if (bitmap != null)
            preview.SetInput(sample, bitmap);
        else if (sample.Stamp == 0)
        {
            preview.ClearFrame();
            SetBoneNames("");
        }
        bool compare =
            input.PreviewVisible
            && showBones.Checked
            && sample.Stamp != 0
            && ReferenceEquals(preview.Sample, sample);
        var trace =
            compare && (bitmap != null || previewDirty || now - lastPreview >= 200)
                ? new RigTrace()
                : null;
        renderer.Rig?.Apply(m, renderer.FullBody, .32f, trace);
        renderer.Render(
            preview: trace == null ? null : preview.AvatarBuffer(renderer.ThumbnailSize)
        );
        if (trace != null && renderer.Rig is { } rig)
        {
            var snapshot = rig.Snapshot(trace, sample.Detected);
            preview.SetPose(snapshot, renderer.ViewProjection, renderer.FullBody);
            SetBoneNames(string.Join("  ·  ", snapshot.Bones.Select(b => b.Name)));
            previewDirty = false;
            lastPreview = now;
        }
        if (metrics?.Record(m, sample.Stamp) == true)
        {
            BeginInvoke(Close);
            return;
        }
        if (now - lastStatus >= 250)
        {
            lastStatus = now;
            trackingState.Text = input.Status;
            trackingDetails.Text =
                $"脸 {m.Face.Length} 点     身体 {m.Body.Length} 点     手 {m.Hands.Length} 只     推理 {m.Milliseconds:0.0} ms";
        }
    }
}

internal sealed class MappingDialog : Form
{
    public Dictionary<string, string> Bones { get; } = new();
    public Dictionary<string, string> Morphs { get; } = new();

    public MappingDialog(AvatarRig rig, StudioProfile profile)
    {
        Text = "角色绑定";
        ClientSize = new Size(600, 460);
        StartPosition = FormStartPosition.CenterParent;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(tabs);
        DataGridView Grid(
            string name,
            IEnumerable<string> actual,
            Dictionary<string, string> values,
            string[] canonical
        )
        {
            var tab = new TabPage(name);
            tabs.TabPages.Add(tab);
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = false,
                RowHeadersVisible = false,
            };
            tab.Controls.Add(grid);
            grid.Columns.Add("key", "标准名称");
            grid.Columns[0].ReadOnly = true;
            var list = new DataGridViewComboBoxColumn { HeaderText = "模型名称" };
            list.Items.Add("");
            list.Items.AddRange(actual.Distinct().Order().Cast<object>().ToArray());
            grid.Columns.Add(list);
            foreach (var key in canonical.Distinct())
            {
                int i = grid.Rows.Add(key, "");
                if (values.TryGetValue(key, out var value) && list.Items.Contains(value))
                    grid.Rows[i].Cells[1].Value = value;
            }
            return grid;
        }
        var names = new List<string> { "センター", "上半身", "首", "頭" };
        foreach (var s in new[] { "左", "右" })
        {
            foreach (
                var part in new[] { "目", "腕", "ひじ", "手首", "足", "ひざ", "足首", "つま先" }
            )
                names.Add(s + part);
            foreach (var f in new[] { "親指", "人指", "中指", "薬指", "小指" })
                for (int n = 0; n < 3; n++)
                    names.Add(s + f + (n + (f == "親指" ? 0 : 1)));
        }
        var b = Grid("骨骼", rig.Model.Bones.Select(x => x.Name), profile.Bones, names.ToArray());
        var m = Grid(
            "表情",
            rig.Model.Morphs.Select(x => x.Name),
            profile.Morphs,
            [
                "まばたき",
                "ウィンク",
                "ウィンク右",
                "あ",
                "い",
                "う",
                "え",
                "お",
                "にやり",
                "上",
                "怒り",
            ]
        );
        var save = new Button
        {
            Text = "保存绑定",
            Dock = DockStyle.Bottom,
            Height = 38,
        };
        Controls.Add(save);
        save.Click += (_, _) =>
        {
            void Read(DataGridView g, Dictionary<string, string> d)
            {
                g.EndEdit();
                foreach (DataGridViewRow r in g.Rows)
                    if (
                        r.Cells[1].Value is string v
                        && v.Length > 0
                        && r.Cells[0].Value is string key
                    )
                        d[key] = v;
            }
            Read(b, Bones);
            Read(m, Morphs);
            DialogResult = DialogResult.OK;
        };
    }
}
