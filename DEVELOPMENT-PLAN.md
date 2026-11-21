# 用普通摄像头驱动 MMD 直播

2026-11-21 · Windows x64 · 需求与技术选型草案

## 先确认的使用细节

| 问题 | 暂定方案及其影响 |
| --- | --- |
| 模型是 PMX 还是还有 PMD？是否方便提供一套能本地测试的模型和贴图？如果现成软件已经够用，是否接受每个角色一次格式转换及现有软件自身的技术栈？ | 开发以直接打开 PMX 为首要目标。若可以转换且只需开播，先试 Warudo。手指骨、眼骨和表情变形（morph）以实际模型为准。 |
| 海康威视的完整型号、连接方式、标称 1440p 帧率是什么？ | 暂按普通 USB/UVC 电脑摄像头设计，接收 2560×1440。首次使用时枚举设备实际提供的格式和帧率。网络型号需另选输入方式。 |
| 通常是坐播聊天、同时玩游戏，还是站播跳舞？期望 OBS 输出 1080p60 还是 1440p60？ | 第一阶段优先坐姿半身，建议在 RTX 3080 上以单人、1080p60 渲染、30 次/秒追踪为目标。并行游戏负载另记。 |
| Windows 10 是普通 22H2 还是企业 LTSC？是否有特定 Radeon 型号必须覆盖？ | 将 Windows 10 22H2 x64 纳入实测，另列 Windows 10 LTSC 2021 与 Windows 11 x64。第二台 AMD 真机用于验证跨厂商表现。 |
| 只打算自己直播，还是还要发布、开源或出售软件？ | 直播内容、软件源码、角色素材和推理权重分别核对授权。若需发布软件，优先选允许相应用途的渲染库和权重。 |

## 现有软件

| 候选 | 项目文档已确认的能力 | 对本需求的意义 |
| --- | --- | --- |
| XR Animator Windows 桌面版及 VMC 社区分支 | 上游可用摄像头、图片或视频驱动 MMD，脸、手、身体可组合追踪。桌面版基于 Electron，提供透明 OBS 输出。VMC 分支明确写出窗口/游戏捕获，同页标注早期开发中。 | 用同一个角色和照片做第一轮效果比对。上游只说明了 OBS 透明窗口。分支的 ONNX 推理后端仍在开发，Windows 游戏捕获、可调灯光及两家显卡上的推理都需要核验。上游源码改编采用 CC BY-NC-SA 4.0，作者将使用自有素材生成的视频排除在该源码许可的范围外。 [上游与许可](https://github.com/ButzYung/SystemAnimatorOnline) · [VMC 分支](https://github.com/BashhScriptKid/SystemAnimatorOnline-VMC/blob/master/readme.md) |
| Warudo | 内置 MediaPipe 摄像头面捕与手腕、手指追踪，默认 GPU 加速。支持角色打光。手册列出窗口/游戏捕获和透明输出。官方角色入口为 VRM 或 `.warudo`，MMD 角色可由 Mod SDK 导出。 | 能接受一次转换时，这是已有的半身直播路线。单摄全身可另评估用 XR Animator 通过 VMC 发姿态。 [角色格式](https://docs.warudo.app/zh/docs/assets/character) · [追踪](https://docs.warudo.app/zh/docs/mocap/mediapipe) · [灯光](https://docs.warudo.app/docs/assets/lights) · [OBS 输出](https://docs.warudo.app/docs/tutorials/getting-started) |
| Reze MiPo Desktop | 支持本地 PMX、单图、视频、摄像头、脸、双手和全身。使用 Electron 和 WebGPU 渲染。 | 适合用同一张真人图与同一个 PMX 对照骨骼和表情。OBS 游戏捕获透明与各追踪模型在 AMD、NVIDIA 上的执行情况仍待验证。 [项目说明](https://github.com/BesingBG/reze-mipo-desktop) |

已有软件覆盖了单摄像头驱动 MMD 的主要流程。按全部硬性条件筛选，目前查到的产品仍有架构、角色格式、透明捕获或 GPU 验证的缺项。第一轮先用现成软件跑一张照片和一段视频，结果满足你的开播要求即可采用。

## 复用方案与数据流

- 用 C# 和 .NET 11 搭建 Windows x64 控制与播放程序。2026-11-10 发布的正式版为 11.0.0，SDK 为 11.0.100。Windows 10 普通 22H2 单列项目兼容测试，微软列出的 .NET 11 Windows 10 受支持范围为指定的 LTSC/Enterprise 版本。 [SDK](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) · [Windows 支持表](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions)
- 优先试用 MIT 许可的 Coocoo3D 的 PMX 导入与 Direct3D 12 渲染，先验证无光追的实时管线以及最终输出帧的 Alpha。若 PMD 也是主力格式或上游移植代价太高，评估 MIT 许可的 Saba 模型库及其 D3D11 示例，由 .NET 负责应用逻辑。两者均须通过同一模型的测试。 [Coocoo3D](https://github.com/sselecirPyM/Coocoo3D) · [Saba](https://github.com/benikabocha/saba)
- 图片、视频、摄像头统一输出带时间戳的帧。Media Foundation 在用户选择真机时协商分辨率、像素格式和帧率。GPU 从整帧定位人体，再从原始高分辨率画面裁出脸和双手。推理与渲染分开调度，积压时取最新帧。 [摄像头格式协商](https://learn.microsoft.com/en-us/windows/win32/medfound/how-to-set-the-video-capture-format)
- Windows 10/11 共用 ONNX Runtime 的 DirectML 执行后端。选择实际的 AMD/NVIDIA 硬件适配器。身体及手部先评估 OpenCV Zoo 已发布的 ONNX 模型。脸部还需人脸定位、含眼部特征的关键点模型与表情估计模型。逐个验证算子、输出语义、权重来源和使用条件。记录实际执行设备并阻止推理退回 CPU。CPU 负责设备管理、界面及少量骨骼计算，GPU 负责神经网络、蒙皮和绘制。 [DirectML 支持与限制](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html) · [手部 ONNX](https://github.com/opencv/opencv_zoo/tree/main/models/handpose_estimation_mediapipe) · [身体 ONNX](https://github.com/opencv/opencv_zoo/tree/main/models/pose_estimation_mediapipe) · [面部关键点与表情输出参考](https://ai.google.dev/edge/mediapipe/solutions/vision/face_landmarker)
- 从关键点、表情系数生成局部骨骼旋转和 MMD morph 权重。按模型保存骨骼/表情别名、中性校准、左右方向和强度。导入时列出可驱动的眼、嘴和指关节，例如检测到左眼闭合时应驱动角色本人的左眼，镜像预览只影响显示。有 `まばたき` 但没有左右独立闭眼的模型可以绑定双眼眨眼，有 `あ/い/う/え/お` 的模型可逐项绑定口型。将角色画入带 Alpha 的独立 Direct3D 输出窗口，控制界面和真人预览留在控制窗口。

## 开发和验收顺序

| 阶段 | 交付物 | 验收方法 |
| --- | --- | --- |
| 1. 现成软件与渲染原型 | 用同一 PMX、同一照片比较 XR Animator 与分支。检查现成路线是否已经能开播。进入 .NET 路线时，用可复用渲染器显示角色。 | OBS 30.2.3 的「游戏捕获 → 捕获特定窗口 → 允许透明」叠在棋盘、纯白和纯黑背景上。移出模型区域后应显出底图，角色实体保持遮盖，发丝及半透明贴图没有黑/白边。调整控制窗口时直播画面仍只有角色。验收场景只使用 OBS 自带的游戏捕获和真实 Alpha。 [OBS 游戏捕获](https://obsproject.com/kb/game-capture-source) |
| 2. 文件输入和 GPU 推理 | 图片输入默认开启。导出关键点叠图、表情值、左右手和各阶段耗时。 | 用允许测试的真实照片覆盖正脸、侧脸、手势、同框双手、全身和无人场景。检查眨眼侧别、食指与中指相对伸直、左右手身份与可见关节。公开例图可从 [NASA 宇航员肖像](https://www.nasa.gov/former-astronaut-eileen-collins/) 和 [Moyashi-otaku 的 V 字手势，CC BY 4.0](https://commons.wikimedia.org/wiki/File:A_Peace_sign_photo.jpg) 起步。每个样本记录来源、署名、授权、散列和预期动作。 |
| 3. 半身直播 | 头颈、躯干、肩肘腕、双手各手指，眼球视线、左右眨眼、张嘴、微笑和模型已有的口型。加入中性姿势校准、抑抖及遮挡恢复。 | 用可重复播放的真人短片测试说话、单眼闭合、挥手、握拳、交叉双手和手部出画再返回。保留两路对照录像，可核对侧别及动作同步。 |
| 4. 打光与摄像头接入 | 增加主光、补光、轮廓光，可调方向、颜色、强度，按角色保存。接入海康威视相机供用户手动启用。 | 文件模式用同帧比较调灯前后角色及发梢边缘。真机由用户主动开始本地预览，确认 2560×1440 实际输入模式、持续帧率、断线重连，并用相同 OBS 场景核验。 |
| 5. 全身 | 增加骨盆、髋、膝、踝、脚底锁定及站姿校准。遮挡时暂持最近可靠姿态、再平滑回中，重新入画时平滑恢复。 | 用头脚同框的站立、侧转、抬腿、踏步、双脚交叉、部分出画短片检查脚滑和膝反折。单目深度及身体自遮挡会影响结果，分别记录坐姿与站姿的可用范围。 [单摄身体、脸与手联合捕捉研究](https://arxiv.org/abs/1812.01598) |
| 6. 性能与发行 | 自包含 `win-x64` 包、模型映射预设、推理设备与耗时报告。 | RTX 3080 与一台真实 Radeon 分别跑固定素材。覆盖 Windows 10/11 x64 和安装包。建议先在 RTX 3080 上进行 30 分钟半身回放，目标为 1080p60 输出、每秒 30 次姿态更新、输入帧可用到角色提交显示的耗时在 95% 的帧中 ≤ 100 ms。记录丢帧、显存、实际 GPU 执行和 OBS 渲染延迟。摄像头曝光/USB 传输及直播平台延迟单独记录。 |

## 当前基线

本机只读查询得到 Windows 11 25H2 x64、GeForce RTX 3080、OBS Studio 30.2.3。当前系统默认 `dotnet` 列出的 SDK 为 6.0.428 和 9.0.321，开发阶段可在工作区独立安装并锁定 11.0.100。现阶段的证据为厂商/作者资料及本机环境清单，模型、帧率、透明游戏捕获和摄像头实拍结果留待以上各项验收。

Git 主工作树保存调研基线，`codex/feasibility` 工作树用于原型。引入上游实现时固定提交号并保留许可，让模型导入、GPU 推理和 OBS 输出可以分别回归。
