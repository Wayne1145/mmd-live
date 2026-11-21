# 回归验证

`MmdLive.Checks` 用于创建指定分辨率的回放、检查真实 OBS 黑白底合成，以及测量调灯前后的 RGBA 变化。`MmdLive --probe` 走与直播相同的追踪与渲染代码。所有测试均可选图片或本地视频作为输入。

```powershell
./scripts/fetch-models.ps1
./scripts/dotnet.ps1 build MmdLive.slnx -c Release
./scripts/dotnet.ps1 run --no-build -c Release --project src/MmdLive -- --probe --image ./local/pose.jpg --full --repeat 12 --out ./artifacts/pose
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- light ./assets/studio/StudioPilot.pmx ./artifacts/pose
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- video ./local/thanks.mkv ./local/thanks-1440.avi
```

用于端到端回放时，运行发布版 `MmdLive.exe --studio --video <路径> --metrics <结果目录> --seconds 1800`。结果文件 `playback.json` 每十秒更新，包含真实输入分辨率、画面提交帧率、姿态更新频率和 P95 延迟。延迟从解码帧可用开始计时，到该帧姿态的画面提交结束。单帧静态模式额外导出 RGB 关键点叠图和带透明通道的角色图。

## OBS 游戏捕获验收

用独立的 OBS 便携配置准备一个名为 `MmdQA` 的场景，在其 WebSocket 服务中设置测试口令。在同一 Windows 会话中运行本软件并打开一张静态照片。以下脚本连接本机端口，通过 OBS 的窗口列表选取 `MMD Live — OBS Output`，生成黑底、白底和单独游戏源的截图。测试设置及 OBS 统计写入 `obs.json`。

```powershell
node tests/obs-capture.mjs <便携实例的obs-websocket配置JSON路径> ./artifacts/obs-alpha
./scripts/dotnet.ps1 run --no-build -c Release --project tests/MmdLive.Checks -- alpha ./artifacts/obs-alpha
```

像素检查对透明区域比较黑白底色，并对不透明区域比较两次捕获的相同颜色。输出还包含半透明像素计数。运行时应使用同一静态人物姿态。
