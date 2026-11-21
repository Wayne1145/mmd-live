# 回归验证

`MmdLive.Checks` 用于创建指定分辨率的回放、检查真实 OBS 黑白底合成，以及测量调灯前后的 RGBA 变化。`MmdLive --probe` 走与直播相同的追踪与渲染代码。所有测试均可选图片或本地视频作为输入。

```powershell
./scripts/fetch-models.ps1
./scripts/dotnet.ps1 build MmdLive.slnx -c Release
./scripts/dotnet.ps1 run --no-build -c Release --project src/MmdLive -- --probe --image ./local/pose.jpg --full --repeat 12 --out ./artifacts/pose
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- light ./assets/studio/StudioPilot.pmx ./artifacts/pose
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- video ./local/thanks.mkv ./local/thanks-1440.avi
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- upright ./local/thanks-1440.avi ./assets/models
```

用于端到端回放时，运行发布版 `MmdLive.exe --studio --video <路径> --metrics <结果目录> --seconds 1800`。结果文件 `playback.json` 每十秒更新，包含真实输入分辨率、画面提交帧率、姿态更新频率和 P95 延迟。延迟从解码帧可用开始计时，到该帧姿态的画面提交结束。单帧静态模式额外导出 RGB 关键点叠图和带透明通道的角色图。

## 本地追踪预览

在带图形桌面的 Windows 工作站上运行以下命令。使用一张脸和手可见的照片，测试控制台显示当前输入，勾选后出现两组骨骼，取消勾选后恢复相同的原图像素，停止后清除画面。

```powershell
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- preview ./assets/studio/StudioPilot.pmx ./local/woman_hands.jpg ./artifacts/preview
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- preview ./assets/studio/StudioPilot.pmx ./local/thanks-1440.avi ./artifacts/preview-video --video
```

## 当前帧的遮挡与对齐

使用有脸和双手的真人照片，下面的回归会生成一段本地遮挡录像，按实际输入链路检查遮住右手、左手、脸、整幅画面和重新入画时的驱动骨名。

```powershell
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- occlusion ./assets/studio/StudioPilot.pmx ./local/woman_hands.jpg ./artifacts/occlusion
```

图片诊断的 `overlay.png` 使用与控制台相同的可见关节点和肩宽对齐。青线为当前帧识别结果，橙线为解算后模型的实际骨骼。按肩宽对齐时，伸向镜头的手会受透视和角色骨长影响。

## 连续追踪回放

`observe` 将本地视频逐帧送入 DirectML，向既有目录写入每帧的原始观测。对照片可加重复次数检查静止漂移。`replay` 将同一组观测送入半身直播解算器，记录头及左右上臂、前臂和手腕相邻帧的局部与世界转角，以及按肩宽投影的腕点位置。附上对应视频时使用视频帧率并可指定叠图帧号，单独读观测时按 30 Hz 回放。

```powershell
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- observe ./local/thanks-1440.avi ./artifacts/video.ndjson
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- observe ./local/woman_hands.jpg ./artifacts/still.ndjson 48
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- replay ./assets/studio/StudioPilot.pmx ./artifacts/video.ndjson ./artifacts/replay ./local/thanks-1440.avi 6,12,27,34,39
```

`metrics.ndjson` 的角度单位为度，`wristErrorPct` 的单位为输入画面宽度的百分比，无手部观测的帧记为 `null`。比较前后版本时，同时检查可见率、最长连续缺失、快速抬手的到达帧以及腕点误差。

## OBS 游戏捕获验收

用独立的 OBS 便携配置准备一个名为 `MmdQA` 的场景，在其 WebSocket 服务中设置测试口令。在同一 Windows 会话中运行本软件并打开一张静态照片。以下脚本连接本机端口，通过 OBS 的窗口列表选取 `MMD Live — OBS Output`，生成黑底、白底和单独游戏源的截图。测试设置及 OBS 统计写入 `obs.json`。

```powershell
node tests/obs-capture.mjs <便携实例的obs-websocket配置JSON路径> ./artifacts/obs-alpha
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- alpha ./artifacts/obs-alpha
```

像素检查对透明区域比较黑白底色，并对不透明区域比较两次捕获的相同颜色。输出还包含半透明像素计数。运行时应使用同一静态人物姿态。

## Windows 10、Windows 11 与实机显卡验证

`Test-ReleaseGpu.ps1` 直接运行已解压的 Windows 发布包。它使用同一张可见人脸和双手的照片，在 1920×1080 的 RGBA 帧上检查角色由中性姿势变为照片中的姿势。另两组测试分别使用竖拇指和伸食指照片及其水平镜像，核对左右侧别和每只手的 21 个归一化关节坐标。参照数据来自 Google MediaPipe，脚本校验每份样本的 SHA-256。

```powershell
.\tests\Test-ReleaseGpu.ps1 -ReleaseDirectory 'D:\MmdLive' -ResultsDirectory '.\artifacts\verification' -Vendor AMD
```

NVIDIA 机器使用 `-Vendor NVIDIA`。多显卡设备可用 `-AdapterIndex 1` 等 DXGI 序号选择 GPU。`-ModelPath` 可以指定要验收的真实 PMX 或 PMD，`-FixtureDirectory` 可以指定已下载的固定照片和参照文件。结果文件 `station.json` 记录实际 Windows build、显卡 PCI 厂商、驱动、程序集散列、输入散列、透明像素及姿势变化，并列出四个手势案例的归一化坐标误差。实际摄像头的采集模式和 AMD、Windows 10 的运行情况需要分别在目标设备上测量。
