# 第三方组件与测试素材

MMD Live 应用和 Studio Pilot 示例角色采用仓库根目录的 MIT 许可。第三方包、权重和素材各自采用原作者的条款。发布包的 `licenses` 目录包含对应文本。

| 组件 | 版本或来源 | 用途 |
| --- | --- | --- |
| [Coocoo3D](https://github.com/sselecirPyM/Coocoo3D/tree/9ef92306bf08269d6b4f2780f4ad62501324cbfe) | MIT。原文件 `Coocoo3D.Extensions/FileFormat/PMXFormat.cs`，副本及许可在 `vendor/Coocoo3D`。本项目调整了 PMX 2.1 读取与数量检查。 | PMX 数据结构与解析 |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) 和 [Microsoft DirectML](https://github.com/microsoft/DirectML) | ORT 1.24.4，MIT。DirectML 1.15.4 随 Microsoft 软件许可与 ThirdPartyNotices 分发。 | GPU 神经网络执行 |
| [OpenCvSharp](https://github.com/shimat/opencvsharp)、[OpenCV](https://github.com/opencv/opencv)、[FFmpeg](https://ffmpeg.org/) | OpenCvSharp4 4.13.0.20261107，Apache-2.0。使用上游 Windows 运行包中分离的 OpenCV 和 FFmpeg 视频插件。 | 图像、摄像头和视频文件 |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)、[Vortice.Mathematics](https://github.com/amerkoleci/Vortice.Mathematics)、[SharpGenTools](https://github.com/SharpGenTools/SharpGenTools) | MIT。具体绑定及传递版本记录于 `packages.lock.json`。 | Direct3D、DXGI 和着色器编译 |
| [Pfim](https://github.com/nickbabcock/Pfim) | 0.11.4，MIT。 | DDS 和 TGA 纹理解码 |
| [Microsoft .NET](https://github.com/dotnet/runtime) | .NET 11，自包含包保留官方许可和第三方告知文本。 | Windows 桌面运行时 |

## 推理权重

- 人体检测、姿态、手掌与手部关键点模型来自 [OpenCV Zoo](https://github.com/opencv/opencv_zoo/tree/48b10fa505aeb51fbd127fc60a5c660040e823b7/models)，使用 MediaPipe/Google 的模型。各模型目录附有 Apache 2.0 许可。
- 人脸检测、478 点网格、52 项表情估计及 146 点索引来自 [MediaPipe Face Landmarker 的 ONNX 转换](https://huggingface.co/t9749/mediapipe-face-landmarker-onnx/tree/5c3cc92b90fee044d05504404ed52983b3fd8eab)。原始权重由 Google 以 Apache 2.0 发布。
- 下载地址、固定版本、大小和 SHA-256 列于 `assets/model-manifest.json`。

## 测试输入

- `astronaut.png` 为 NASA 的 Eileen Collins 照片，取自 [scikit-image 数据集](https://github.com/scikit-image/scikit-image/blob/v0.25.2/skimage/data/astronaut.png)。
- `pose.jpg`、`woman_hands.jpg`、`right_hands.jpg` 及 `thumb_up.jpg` 为 [MediaPipe 的测试数据](https://github.com/google-ai-edge/mediapipe/tree/master/mediapipe/tasks/testdata/vision)，从 Google 的 `mediapipe-assets` 桶获取。
- 手势回放采用 [Intel Sample Videos 的 thanks.mkv](https://github.com/intel-iot-devkit/sample-videos/blob/57978890822836f2b4743852f04f62fc511757e4/asl_gestures/thanks.mkv)。本地性能测试将其转换为 2560×1440 的循环视频。
- 真实角色兼容测试使用 [Gene](https://github.com/mmdagent-ex/gene/tree/c7eace43dffaccff6ad0597433ef85fa57c91e03)，CC BY 4.0。署名为 **CG-CA Gene (c) 2023 by Nagoya Institute of Technology, Moonshot R&D Goal 1 Avatar Symbiotic Society**。
