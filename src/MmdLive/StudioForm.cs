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
    readonly TrackingPreview preview = new();
    readonly CheckBox showBones = new()
    {
        Text = "显示骨骼",
        AutoSize = true,
        Margin = new Padding(0, 7, 12, 0),
    };
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
        camera = Choice();
    readonly Segmented source = new("摄像头", "视频文件", "图片"),
        mode = new("半身", "全身"),
        lightSelector = new("主光", "补光", "轮廓光");
    readonly Slider azimuth = new("水平方位", -180, 180, "°"),
        elevation = new("仰角", -90, 90, "°"),
        power = new("强度", 0, 250, "%"),
        zoom = new("缩放", 50, 200, "%");
    readonly Button color = ButtonFor("光色"),
        startStop = new()
        {
            Height = 44,
            Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            AccessibleName = "开始或停止输入",
            Margin = new Padding(0, 4, 0, 4),
        },
        chooseFile = ButtonFor("选择文件…");
    readonly Label modelName = ValueLabel(),
        modelStats = Hint(""),
        fileName = Hint("尚未选择文件"),
        noCamera = Hint(""),
        trackingState = ValueLabel(),
        trackingDetails = ValueLabel(),
        notice = Hint("");
    readonly StatusChip faceChip = new("脸"),
        bodyChip = new("身体"),
        leftHandChip = new("左手"),
        rightHandChip = new("右手");
    readonly Panel cameraRow = new() { Height = 36, Margin = new Padding(0, 0, 0, 4) },
        fileRow = new() { Height = 36, Margin = new Padding(0, 0, 0, 4), Visible = false };
    readonly ToolTip tips = new() { InitialDelay = 300, AutoPopDelay = 12000 };
    string? chosenVideo,
        chosenImage;
    bool inputActive;
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
        ClientSize = new Size(1180, 740);
        MinimumSize = new Size(960, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Theme.Surface;
        ForeColor = Theme.Ink;
        BuildLayout();
        Load += (_, _) => ScaleForDpi(this);
        var gpus = GraphicsAdapter.List();
        if (gpus.Count == 0)
            throw new InvalidOperationException("未找到 Direct3D 硬件显卡");
        foreach (var gpu in gpus)
            adapter.Items.Add(gpu);
        adapter.SelectedIndex = 0;
        mode.SelectedIndexChanged += (_, _) => ApplyProfile();
        zoom.Bar.ValueChanged += (_, _) => ApplyProfile();
        lightSelector.SelectedIndexChanged += (_, _) => RefreshLight();
        azimuth.Bar.ValueChanged += (_, _) => ChangeLight();
        elevation.Bar.ValueChanged += (_, _) => ChangeLight();
        power.Bar.ValueChanged += (_, _) => ChangeLight();
        source.SelectedIndexChanged += (_, _) => UpdateInputUi();
        startStop.Click += (_, _) => ToggleInput();
        chooseFile.Click += (_, _) => ChooseFile();
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
                // After DPI scaling the default size can exceed a small screen.
                Size = new Size(Math.Min(Width, area.Width * 9 / 10), Math.Min(Height, area.Height * 9 / 10));
                Location = new Point(
                    area.Left + (area.Width - Width) / 2,
                    area.Top + (area.Height - Height) / 2
                );
                output.Location = new Point(
                    Math.Max(area.Left, area.Right - output.Width - 22),
                    area.Top + 42
                );
                output.Show();
                modelFile = Arg("--model") ?? Program.Demo;
                await RebuildRenderer();
                RefreshCameras();
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

    const int SidebarWidth = 300;

    // Fonts follow the monitor DPI but code-built layouts do not auto-scale here, so fixed
    // sizes (written for 96 DPI) clipped their text at 150-200%. Scale them once on load.
    internal static void ScaleForDpi(Control root)
    {
        float factor = root.DeviceDpi / 96f;
        if (factor > 1.01f)
            root.Scale(new SizeF(factor, factor));
    }

    static ComboBox Choice() =>
        new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.System,
        };

    static Label ValueLabel() =>
        new()
        {
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Height = 25,
        };

    static Label Hint(string text) =>
        new()
        {
            Text = text,
            ForeColor = Theme.Muted,
            AutoSize = true,
            MaximumSize = new Size(SidebarWidth, 0),
            Margin = new Padding(0, 0, 0, 6),
        };

    static Button ButtonFor(string text)
    {
        var b = new Button
        {
            Text = text,
            Height = 32,
            Margin = new Padding(0, 0, 6, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        return b;
    }

    // Buttons share the sidebar width equally, one row high.
    static TableLayoutPanel ButtonRow(params Button[] buttons)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = Math.Max(2, buttons.Length),
            RowCount = 1,
            Height = 34,
            Margin = new Padding(0, 0, 0, 6),
        };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < row.ColumnCount; i++)
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / row.ColumnCount));
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Dock = DockStyle.Fill;
            buttons[i].Margin = new Padding(i == 0 ? 0 : 3, 0, i == buttons.Length - 1 ? 0 : 3, 0);
            row.Controls.Add(buttons[i], i, 0);
        }
        return row;
    }

    Button Action(string text, Action action, string? tip = null)
    {
        var b = ButtonFor(text);
        b.Click += (_, _) => action();
        if (tip != null)
            tips.SetToolTip(b, tip);
        return b;
    }

    // A titled group in the sidebar. Children stretch to the sidebar width.
    static Control Section(string title, params Control[] rows)
    {
        var box = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(0, 0, 0, 10),
        };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SidebarWidth));
        box.Controls.Add(
            new Label
            {
                Text = title,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                ForeColor = Theme.Accent,
                Margin = new Padding(0, 6, 0, 8),
            }
        );
        foreach (var row in rows)
        {
            if (row is not Label)
                row.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            box.Controls.Add(row);
        }
        // A hairline under each group.
        box.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, box.Height - 1, box.Width, box.Height - 1);
        };
        return box;
    }

    static Control Labeled(string label, Control value)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Height = 34,
            Margin = new Padding(0, 0, 0, 4),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(
            new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
            },
            0,
            0
        );
        value.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        row.Controls.Add(value, 1, 0);
        return row;
    }

    void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SidebarWidth + 44));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // Sidebar: everything the performer sets up, top to bottom in the order it is needed.
        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Panel,
            Padding = new Padding(18, 10, 8, 12),
            Margin = Padding.Empty,
        };
        root.Controls.Add(sidebar, 0, 0);
        sidebar.Controls.Add(
            new Label
            {
                Text = "MMD Live",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
                Margin = new Padding(0, 2, 0, 8),
            }
        );

        var refresh = Action("刷新", RefreshCameras, "重新读取已连接的摄像头");
        refresh.Dock = DockStyle.Right;
        camera.Dock = DockStyle.Fill;
        cameraRow.Controls.Add(camera);
        cameraRow.Controls.Add(refresh);
        chooseFile.Dock = DockStyle.Right;
        fileName.Dock = DockStyle.Fill;
        fileName.AutoSize = false;
        fileName.AutoEllipsis = true;
        fileName.TextAlign = ContentAlignment.MiddleLeft;
        fileRow.Controls.Add(fileName);
        fileRow.Controls.Add(chooseFile);
        tips.SetToolTip(startStop, "开始或停止当前输入源");
        sidebar.Controls.Add(Section("输入源", source, cameraRow, noCamera, fileRow, startStop));

        modelName.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        modelName.Height = 26;
        sidebar.Controls.Add(
            Section(
                "角色",
                modelName,
                modelStats,
                ButtonRow(
                    Action("打开模型…", OpenModel, "载入 .pmx 或 .pmd 角色"),
                    Action("绑定设置…", EditMapping, "骨骼或表情名不标准时，手动指定对应名称")
                )
            )
        );

        sidebar.Controls.Add(
            Section(
                "画面",
                mode,
                zoom,
                ButtonRow(
                    Action(
                        "显示输出窗口",
                        () =>
                        {
                            output.Show();
                            output.Activate();
                        }
                    )
                ),
                Hint("OBS：游戏捕获 → 捕获特定窗口 → 「MMD Live — OBS Output」，勾选「允许透明」")
            )
        );

        sidebar.Controls.Add(
            Section(
                "追踪",
                ButtonRow(Action("校准中性姿势", Calibrate)),
                Hint("正对镜头、表情放松时点击，角色会以此为默认表情和姿势。"),
                notice
            )
        );

        color.TextAlign = ContentAlignment.MiddleCenter;
        sidebar.Controls.Add(
            Section(
                "灯光",
                lightSelector,
                azimuth,
                elevation,
                power,
                Labeled("光色", color),
                ButtonRow(
                    Action(
                        "恢复默认灯光",
                        () =>
                        {
                            profile.Lights = new();
                            RefreshLight();
                            ApplyProfile();
                        }
                    )
                )
            )
        );

        sidebar.Controls.Add(Section("高级", Labeled("显卡", adapter)));
        notice.Visible = false;
        // Scaling and focus can leave the sidebar scrolled; always open at the input section.
        Shown += (_, _) => sidebar.AutoScrollPosition = Point.Empty;

        // Stage: tracking status on top, the live preview below.
        var stage = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14, 10, 16, 14),
            Margin = Padding.Empty,
        };
        stage.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        stage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(stage, 1, 0);

        var status = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
        };
        var chips = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        chips.Controls.AddRange([faceChip, bodyChip, leftHandChip, rightHandChip]);
        trackingDetails.AutoSize = true;
        trackingDetails.ForeColor = Theme.Muted;
        trackingDetails.Margin = new Padding(6, 6, 0, 0);
        chips.Controls.Add(trackingDetails);
        trackingState.Dock = DockStyle.Fill;
        trackingState.Font = new Font("Microsoft YaHei UI", 10f);
        status.Controls.Add(trackingState, 0, 0);
        status.Controls.Add(chips, 0, 1);
        stage.Controls.Add(status, 0, 0);

        var previewHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Margin = Padding.Empty,
            Padding = new Padding(1),
        };
        var previewBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(10, 2, 6, 0),
            WrapContents = false,
            BackColor = Theme.Panel,
        };
        previewBar.Controls.Add(showBones);
        tips.SetToolTip(showBones, "对照真人关键点和角色骨骼，排查动作不对的部位");
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
                    Margin = new Padding(4, 8, 8, 0),
                }
            );
        previewHost.Controls.Add(preview);
        previewHost.Controls.Add(boneNames);
        previewHost.Controls.Add(previewBar);
        stage.Controls.Add(previewHost, 0, 1);

        showBones.CheckedChanged += (_, _) =>
        {
            preview.ShowBones = showBones.Checked;
            boneNames.Visible = showBones.Checked;
            SetBoneNames("");
            previewDirty = true;
            if (!loading)
                profile.ShowBones = showBones.Checked;
        };
        Resize += (_, _) => UpdatePreviewVisibility();
        UpdateInputUi();
    }

    // Input source workflow: pick a source, then one button starts or stops it.
    void UpdateInputUi()
    {
        bool cameraSource = source.SelectedIndex == 0;
        cameraRow.Visible = cameraSource;
        noCamera.Visible = cameraSource && noCamera.Text.Length > 0;
        fileRow.Visible = !cameraSource;
        var path = source.SelectedIndex == 1 ? chosenVideo : chosenImage;
        fileName.Text = path is null ? "尚未选择文件" : Path.GetFileName(path);
        tips.SetToolTip(fileName, path ?? "");
        startStop.Text = inputActive ? "■  停止" : "▶  开始";
        startStop.BackColor = inputActive ? Theme.Danger : Theme.Accent;
        startStop.FlatAppearance.BorderSize = 0;
    }

    void ToggleInput()
    {
        if (inputActive)
        {
            StopInput();
            return;
        }
        switch (source.SelectedIndex)
        {
            case 0:
                StartCamera();
                break;
            case 1 when chosenVideo is { } video:
                _ = WithSwitch(() => StartInput(new(SourceKind.Video, video)));
                break;
            case 2 when chosenImage is { } image:
                _ = WithSwitch(() => StartInput(new(SourceKind.Image, image)));
                break;
            default:
                ChooseFile();
                break;
        }
    }

    void ChooseFile()
    {
        bool video = source.SelectedIndex == 1;
        using var d = new OpenFileDialog
        {
            Filter = video
                ? "视频|*.mp4;*.mkv;*.avi;*.mov;*.webm"
                : "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
        };
        if (d.ShowDialog(this) != DialogResult.OK || input == null)
            return;
        if (video)
            chosenVideo = d.FileName;
        else
            chosenImage = d.FileName;
        UpdateInputUi();
        // Choosing a file means "use this now".
        _ = WithSwitch(() =>
            StartInput(new(video ? SourceKind.Video : SourceKind.Image, d.FileName))
        );
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
        zoom.Value = (int)MathF.Round(profile.Zoom * 100);
        loading = false;
        RefreshLight();
        ApplyProfile();
        modelName.Text = string.IsNullOrWhiteSpace(rig.Model.Name)
            ? Path.GetFileNameWithoutExtension(path)
            : rig.Model.Name;
        tips.SetToolTip(modelName, path);
        modelStats.Text =
            $"{rig.Model.Bones.Count} 根骨骼 · {rig.Model.Morphs.Count} 个表情 · {Path.GetFileName(path)}";
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

    void UpdatePreviewVisibility()
    {
        if (input != null)
            input.PreviewVisible = WindowState != FormWindowState.Minimized;
        previewDirty = true;
    }

    async Task StartInput(Source source)
    {
        if (input == null)
            return;
        // Reflect the running source in the sidebar, including sources given on the command line.
        this.source.SelectedIndex = source.Kind switch
        {
            SourceKind.Video => 1,
            SourceKind.Image => 2,
            _ => 0,
        };
        if (source.Kind == SourceKind.Video)
            chosenVideo = source.Path;
        else if (source.Kind == SourceKind.Image)
            chosenImage = source.Path;
        preview.ClearFrame();
        UpdatePreviewVisibility();
        renderer?.Rig?.ResetLiveTracking();
        inputActive = true;
        UpdateInputUi();
        await input.Start(source);
    }

    void StopInput() =>
        _ = WithSwitch(async () =>
        {
            if (input != null)
                await input.Stop();
            preview.ClearFrame();
            inputActive = false;
            UpdateInputUi();
        });

    // Lists camera names only. A camera is opened when the performer presses Start.
    void RefreshCameras()
    {
        string? previous = camera.SelectedItem as string;
        try
        {
            var list = CameraDevices.Names();
            camera.Items.Clear();
            camera.Items.AddRange(list);
            if (list.Length > 0)
                camera.SelectedIndex = Math.Max(0, Array.IndexOf(list, previous));
            noCamera.Text = list.Length > 0 ? "" : "没有检测到摄像头。接好摄像头后点「刷新」。";
        }
        catch (Exception e)
        {
            noCamera.Text = "读取摄像头失败：" + e.GetBaseException().Message;
        }
        UpdateInputUi();
    }

    void StartCamera()
    {
        if (camera.Items.Count == 0)
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
        {
            Notify("校准需要先开始输入，并让脸完整出现在画面中。", Theme.Warning);
            return;
        }
        renderer.Rig.Calibrate(input.Latest);
        profile.Neutral = renderer.Rig.Neutral;
        Save();
        Notify($"已校准中性姿势（{DateTime.Now:HH:mm:ss}）", Theme.Accent);
    }

    void Notify(string text, Color ink)
    {
        notice.Text = text;
        notice.ForeColor = ink;
        notice.Visible = true;
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
        azimuth.Value = (int)MathF.Round(l.Azimuth);
        elevation.Value = (int)MathF.Round(l.Elevation);
        power.Value = (int)MathF.Round(l.Power * 100);
        color.BackColor = Color.FromArgb(l.Rgb | unchecked((int)0xff000000));
        color.ForeColor = color.BackColor.GetBrightness() > .5 ? Color.Black : Color.White;
        color.Text = $"#{l.Rgb & 0xffffff:X6}  ·  点击更改";
        loading = false;
    }

    void ChangeLight()
    {
        if (loading)
            return;
        SetLight(
            new(
                azimuth.Value,
                elevation.Value,
                power.Value / 100f,
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
        renderer.Zoom = profile.Zoom = zoom.Value / 100f;
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
        var driven = renderer.Rig?.ApplySample(sample, renderer.FullBody, Stopwatch.GetTimestamp());
        var trace =
            compare && (bitmap != null || previewDirty || now - lastPreview >= 200) ? driven : null;
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
            bool live = sample.Stamp != 0;
            trackingState.Text = inputActive || live ? input.Status : "未开始 · 在左侧选择输入源后点击「开始」";
            faceChip.Set(live && m.Face.Length > 0);
            bodyChip.Set(live && m.Body.Length > 0);
            leftHandChip.Set(live && m.Hands.Any(h => h.Left));
            rightHandChip.Set(live && m.Hands.Any(h => !h.Left));
            trackingDetails.Text = live ? $"推理 {m.Milliseconds:0.0} ms" : "";
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
        Font = new Font("Microsoft YaHei UI", 9f);
        Load += (_, _) => StudioForm.ScaleForDpi(this);
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
