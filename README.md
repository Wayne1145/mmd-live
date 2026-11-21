# MMD Live

用 Windows 电脑上的普通 USB 摄像头驱动 PMX、PMD 角色，通过 OBS 的游戏捕获进行透明背景直播。眼睛、嘴巴、手指、上半身与腿部使用同一帧视频的关键点。软件提供半身和全身两种构图，可以直接用照片试动作，也可以循环播放本地视频。

## 第一次使用

1. 从 [GitHub Releases](https://github.com/Wayne1145/mmd-live/releases/latest) 下载 Windows x64 压缩包，解压到本地目录，双击 `MmdLive.exe`。压缩包包含 .NET 11 桌面运行时、七个追踪模型及示例角色 Studio Pilot。
2. 在「直播控制」选择图形设备。首先点击「导入图片」，选一张能看清脸和双手的真人照片。独立的 `MMD Live — OBS Output` 窗口会显示角色的动作。
3. 点击「打开模型」选择自己的 `.pmx` 或 `.pmd`。保留模型和贴图的相对目录。角色使用非标准骨骼或表情名称时，打开「绑定设置」指定对应名称。闭单眼需要模型有独立的左右眼闭合变形。
4. 进行直播时点击「读取设备列表」，选择摄像头，再点「开始摄像头」。状态栏显示实际读到的宽、高和采集帧率。对着镜头自然平视，点击「校准中性姿势」。站播放全身时，将脚也放入画面并切换到「全身」。
5. 在 OBS 中添加「游戏捕获」，选择「捕获特定窗口」，选中 `MmdLive.exe` 的 `MMD Live — OBS Output`，勾选「允许透明」。在该源下方放置游戏、图片或色源即可合成。控制台和输出窗口可以独立移动。
6. 在「虚拟灯光」选择主光、补光或轮廓光，调整方向、颜色和强度。退出时会按角色保存灯光、构图、绑定和中性表情。

## 看清追踪和模型的差异

开始摄像头、播放视频或导入照片后，控制台会打开「追踪预览」。勾选「显示骨骼」后，青色线条显示当前输入帧识别到的脸、身体和手指。橙色线条显示角色上实际受驱动的骨骼。叠在真人画面上的橙线优先按肩宽对齐，有效参照也可以是眼距、髋宽或手掌。这个叠加用于对照动作方向。

右侧可以直接查看角色和骨骼，底部可以查看本帧的模型骨名。将指针移到橙色关节上可查看对应的骨名。当前帧丢失脸或单手时，对应的骨线和骨名会立即消失。手指部分出画时仅绘制仍在画面内的关节段。OBS 游戏捕获继续选取独立的 `MMD Live — OBS Output` 窗口。勾选状态随角色在退出时保存。

半身模式保留躯干的中性前后倾角，用双肩控制侧倾和转向。肩部与手腕同时可见时，手臂解算以同侧手掌的腕点为目标，保留模型的上臂和前臂长度。全身模式继续使用三维躯干倾角。

## 运行与兼容

- Windows x64、支持 DirectX 12 的 AMD 或 NVIDIA 显卡。追踪使用 ONNX Runtime DirectML，并启用禁止 CPU 回退的会话选项。Direct3D 11 绘制蒙皮、顶点表情和透明材质，CPU 负责视频解码、输入裁切、骨架解算和界面。
- 程序请求 2560×1440 输入，以原始画面裁取手和脸，输出固定为 1920×1080。摄像头启动由用户控制。图片、视频和摄像头画面在本地处理。
- 本地已验证 Windows 11 25H2 x64、GeForce RTX 3080 和 OBS 30.2.3。Windows 10 x64、Radeon，以及具体 USB 摄像头的可用模式需在对应设备上确认。部分 Windows 10 版本的 .NET 厂商支持范围见[微软系统支持表](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。
- PMX 组、顶点和骨骼表情用于角色驱动。SDEF 和 QDEF 权重使用线性蒙皮近似。首版的头发、裙摆随骨架移动，尚未实现 MMD 刚体物理和 MME 特效。摄像头持续看得见关节时，单目动作最稳定。

## 从源码构建

安装 Windows x64 的 .NET SDK 11.0.100 或同功能带的修订版本。在 PowerShell 中运行以下命令。NuGet 使用包锁文件，模型下载脚本验证每个文件的 SHA-256。

```powershell
./scripts/fetch-models.ps1
./scripts/dotnet.ps1 restore MmdLive.slnx --locked-mode
./scripts/dotnet.ps1 build MmdLive.slnx --no-restore -c Release
./scripts/publish.ps1
```

自包含程序位于 `artifacts/publish/win-x64`。文件模式的诊断命令会保存角色图、真人和模型同帧的 `overlay.png`、关键点叠图、原始追踪结果、实际显卡名和各帧的推理耗时。

```powershell
$exe = './artifacts/publish/win-x64/MmdLive.exe'
& $exe --probe --image 'D:\photos\portrait.jpg' --model 'D:\avatars\avatar.pmx' --repeat 12 --out './artifacts/probe'
```

测试样本、复现方法及原作者署名见 `tests/README.md` 和 `THIRD-PARTY.md`。追踪权重不进入 Git 历史，发布 ZIP 已包含经过哈希校验的完整权重。
