using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Coocoo3D.Extensions.FileFormat;
using MmdLive.Rig;
using OpenCvSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace MmdLive.Rendering;

internal sealed record Light(float Azimuth, float Elevation, float Power, int Rgb)
{
    public Vector4 Direction
    {
        get
        {
            float x = Azimuth * MathF.PI / 180,
                y = Elevation * MathF.PI / 180;
            return new(MathF.Sin(x) * MathF.Cos(y), MathF.Sin(y), -MathF.Cos(x) * MathF.Cos(y), 0);
        }
    }
    public Vector4 Color =>
        new(
            Power * ((Rgb >> 16) & 255) / 255f,
            Power * ((Rgb >> 8) & 255) / 255f,
            Power * (Rgb & 255) / 255f,
            0
        );
}

internal sealed class StudioLights
{
    public Light Key { get; set; } = new(-28, 38, .8f, 0xffeee5);
    public Light Fill { get; set; } = new(48, 12, .4f, 0xc4d9ff);
    public Light Rim { get; set; } = new(150, 35, .45f, 0xffd6c5);
}

internal sealed record GraphicsAdapter(int Index, string Name, uint Vendor)
{
    public override string ToString() => Name;

    public static List<GraphicsAdapter> List()
    {
        using var factory = CreateDXGIFactory1<IDXGIFactory2>();
        var result = new List<GraphicsAdapter>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 a).Success; i++)
            using (a)
            {
                var d = a.Description1;
                if ((d.Flags & AdapterFlags.Software) == 0 && d.VendorId != 0x1414)
                    result.Add(new((int)i, d.Description.TrimEnd('\0'), d.VendorId));
            }
        return result.OrderByDescending(a => a.Vendor is 0x10de or 0x1002).ToList();
    }
}

internal sealed unsafe class Renderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex
    {
        public Vector3 P,
            N;
        public Vector2 Uv;
        public int B0,
            B1,
            B2,
            B3;
        public Vector4 W;
        public uint Start,
            Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    record struct Delta(Vector3 Shift, uint Morph);

    [StructLayout(LayoutKind.Sequential)]
    record struct Scene(
        Matrix4x4 Vp,
        Vector4 KeyDir,
        Vector4 KeyColor,
        Vector4 FillDir,
        Vector4 FillColor,
        Vector4 RimDir,
        Vector4 RimColor
    );

    [StructLayout(LayoutKind.Sequential)]
    record struct Material(Vector4 Diffuse, Vector4 Ambient);

    sealed record Draw(
        int From,
        int Count,
        Material Surface,
        ID3D11ShaderResourceView Image,
        bool Transparent,
        float Distance
    );

    readonly List<IDisposable> owned = [];
    readonly List<IDisposable> modelResources = [];
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly IDXGISwapChain1? chain;
    readonly ID3D11Texture2D target;
    readonly ID3D11RenderTargetView targetView;
    readonly ID3D11DepthStencilView depthView;
    readonly ID3D11Buffer scene,
        material;
    readonly ID3D11DepthStencilState opaqueDepth,
        blendDepth;
    ID3D11Buffer? boneBuffer,
        weightBuffer;
    readonly List<Draw> draws = [];
    readonly int width,
        height;
    public AvatarRig? Rig { get; private set; }
    public StudioLights Lights { get; set; } = new();
    public bool FullBody { get; set; }
    public float Zoom { get; set; } = 1;

    public Renderer(nint window, int adapter, int width, int height)
    {
        this.width = width;
        this.height = height;
        using var factory = CreateDXGIFactory1<IDXGIFactory2>();
        factory.EnumAdapters1((uint)adapter, out IDXGIAdapter1 selected).CheckError();
        using (selected)
            D3D11CreateDevice(
                    selected,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0],
                    out device,
                    out _,
                    out context
                )
                .CheckError();
        owned.Add(device);
        owned.Add(context);
        if (window != 0)
        {
            var d = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = SampleDescription.Default,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
            };
            chain = Own(factory.CreateSwapChainForHwnd(device, window, d));
            factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
            target = Own(chain.GetBuffer<ID3D11Texture2D>(0));
        }
        else
            target = Own(
                device.CreateTexture2D(
                    Format.B8G8R8A8_UNorm,
                    (uint)width,
                    (uint)height,
                    mipLevels: 1,
                    bindFlags: BindFlags.RenderTarget
                )
            );
        targetView = Own(device.CreateRenderTargetView(target));
        var depth = Own(
            device.CreateTexture2D(
                Format.D24_UNorm_S8_UInt,
                (uint)width,
                (uint)height,
                mipLevels: 1,
                bindFlags: BindFlags.DepthStencil
            )
        );
        depthView = Own(device.CreateDepthStencilView(depth));
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("MmdLive.Rendering.Model.hlsl")!;
        using var read = new StreamReader(stream);
        var source = read.ReadToEnd();
        var vs = Vortice.D3DCompiler.Compiler.Compile(source, "VSMain", "Model.hlsl", "vs_5_0");
        var ps = Vortice.D3DCompiler.Compiler.Compile(source, "PSMain", "Model.hlsl", "ps_5_0");
        context.VSSetShader(Own(device.CreateVertexShader(vs.Span)));
        context.PSSetShader(Own(device.CreatePixelShader(ps.Span)));
        InputElementDescription[] layout =
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
            new("BLENDINDICES", 0, Format.R32G32B32A32_SInt, 32, 0),
            new("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 48, 0),
            new("TEXCOORD", 1, Format.R32G32_UInt, 64, 0),
        ];
        context.IASetInputLayout(Own(device.CreateInputLayout(layout, vs.Span)));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.RSSetState(Own(device.CreateRasterizerState(RasterizerDescription.CullNone)));
        context.RSSetViewport(new Viewport(width, height));
        context.PSSetSampler(0, Own(device.CreateSamplerState(SamplerDescription.LinearWrap)));
        var blend = BlendDescription.AlphaBlend;
        blend.RenderTarget[0].SourceBlend = Blend.SourceAlpha;
        blend.RenderTarget[0].DestinationBlend = Blend.InverseSourceAlpha;
        blend.RenderTarget[0].SourceBlendAlpha = Blend.One;
        blend.RenderTarget[0].DestinationBlendAlpha = Blend.InverseSourceAlpha;
        context.OMSetBlendState(Own(device.CreateBlendState(blend)));
        opaqueDepth = Own(device.CreateDepthStencilState(DepthStencilDescription.Default));
        blendDepth = Own(device.CreateDepthStencilState(DepthStencilDescription.DepthRead));
        scene = Own(device.CreateBuffer((uint)sizeof(Scene), BindFlags.ConstantBuffer));
        material = Own(device.CreateBuffer((uint)sizeof(Material), BindFlags.ConstantBuffer));
        context.VSSetConstantBuffer(0, scene);
        context.PSSetConstantBuffer(0, scene);
        context.PSSetConstantBuffer(1, material);
    }

    T Own<T>(T value)
        where T : IDisposable
    {
        owned.Add(value);
        return value;
    }

    T Part<T>(T value)
        where T : IDisposable
    {
        modelResources.Add(value);
        return value;
    }

    ID3D11Buffer Structured<T>(T[] values, int slot)
        where T : unmanaged
    {
        var b = Part(
            device.CreateBuffer(
                values,
                BindFlags.ShaderResource,
                miscFlags: ResourceOptionFlags.BufferStructured,
                structureByteStride: (uint)sizeof(T)
            )
        );
        context.VSSetShaderResource((uint)slot, Part(device.CreateShaderResourceView(b)));
        return b;
    }

    public void Load(AvatarRig rig)
    {
        DisposeModel();
        var model = rig.Model;
        var changes = new List<Delta>?[model.Vertices.Length];
        for (int i = 0; i < model.Morphs.Count; i++)
            if (model.Morphs[i].MorphVertice is { } offsets)
                foreach (var o in offsets)
                {
                    if ((uint)o.VertexIndex < (uint)changes.Length)
                        (changes[o.VertexIndex] ??= []).Add(new(o.Offset, (uint)i));
                }
        var all = new List<Delta>();
        var vertices = new Vertex[changes.Length];
        int Valid(int n) => Math.Clamp(n, 0, model.Bones.Count - 1);
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = model.Vertices[i];
            vertices[i] = new()
            {
                P = v.Coordinate,
                N = v.Normal,
                Uv = v.UvCoordinate,
                B0 = Valid(v.boneId0),
                B1 = Valid(v.boneId1),
                B2 = Valid(v.boneId2),
                B3 = Valid(v.boneId3),
                W = v.Weights,
                Start = (uint)all.Count,
                Count = (uint)(changes[i]?.Count ?? 0),
            };
            if (changes[i] is { } delta)
                all.AddRange(delta);
        }
        context.IASetVertexBuffer(
            0,
            Part(device.CreateBuffer(vertices, BindFlags.VertexBuffer)),
            (uint)sizeof(Vertex)
        );
        context.IASetIndexBuffer(
            Part(device.CreateBuffer(model.TriangleIndexs, BindFlags.IndexBuffer)),
            Format.R32_UInt,
            0
        );
        boneBuffer = Structured(rig.Skin, 0);
        Structured(all.Count > 0 ? all.ToArray() : [default(Delta)], 1);
        weightBuffer = Structured(rig.EffectiveWeights, 2);
        using var white = new Mat(1, 1, MatType.CV_8UC4, Scalar.All(255));
        var fallback = Texture(white);
        var images = new Dictionary<int, (ID3D11ShaderResourceView, bool)>();
        foreach (var mat in model.Materials)
        {
            var image = (View: fallback, Alpha: false);
            int id = mat.TextureIndex;
            if ((uint)id < (uint)model.Textures.Count)
            {
                if (!images.TryGetValue(id, out var cached))
                {
                    cached = (fallback, false);
                    string file = Path.GetFullPath(
                        Path.Combine(
                            rig.Directory,
                            model
                                .Textures[id]
                                .TexturePath.Replace('\\', Path.DirectorySeparatorChar)
                        )
                    );
                    if (
                        file.StartsWith(
                            Path.TrimEndingDirectorySeparator(rig.Directory)
                                + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase
                        ) && File.Exists(file)
                    )
                    {
                        using var source = TextureReader.Decode(file);
                        if (!source.Empty())
                        {
                            using var bgra = new Mat();
                            if (source.Channels() == 4)
                                source.CopyTo(bgra);
                            else
                                Cv2.CvtColor(
                                    source,
                                    bgra,
                                    source.Channels() == 1
                                        ? ColorConversionCodes.GRAY2BGRA
                                        : ColorConversionCodes.BGR2BGRA
                                );
                            bool alpha = false;
                            if (source.Channels() == 4)
                            {
                                using var a = new Mat();
                                Cv2.ExtractChannel(bgra, a, 3);
                                Cv2.MinMaxLoc(a, out double min, out _);
                                alpha = min < 250;
                            }
                            cached = (Texture(bgra), alpha);
                        }
                    }
                    images[id] = cached;
                }
                image = cached;
            }
            float distance = 0;
            for (
                int i = mat.TriangeIndexStartNum;
                i < mat.TriangeIndexStartNum + mat.TriangeIndexNum;
                i++
            )
                distance += model.Vertices[model.TriangleIndexs[i]].Coordinate.Z;
            distance /= Math.Max(1, mat.TriangeIndexNum);
            draws.Add(
                new(
                    mat.TriangeIndexStartNum,
                    mat.TriangeIndexNum,
                    new(mat.DiffuseColor, new(mat.AmbientColor, 0)),
                    image.View,
                    image.Alpha || mat.DiffuseColor.W < .995f,
                    distance
                )
            );
        }
        var sorted = draws
            .OrderBy(x => x.Transparent)
            .ThenByDescending(x => x.Transparent ? x.Distance : 0)
            .ToArray();
        draws.Clear();
        draws.AddRange(sorted);
        Rig = rig;
    }

    ID3D11ShaderResourceView Texture(Mat bgra)
    {
        var tex = Part(
            device.CreateTexture2D(
                Format.B8G8R8A8_UNorm,
                (uint)bgra.Width,
                (uint)bgra.Height,
                mipLevels: 1,
                bindFlags: BindFlags.ShaderResource
            )
        );
        context.UpdateSubresource(tex, 0, null, bgra.Data, (uint)bgra.Step(), 0);
        return Part(device.CreateShaderResourceView(tex));
    }

    public void Render(bool present = true)
    {
        context.OMSetRenderTargets(targetView, depthView);
        context.ClearRenderTargetView(targetView, new Color4(0, 0, 0, 0));
        context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 1, 0);
        if (Rig is { } rig)
        {
            float total = MathF.Max(.001f, rig.Max.Y - rig.Min.Y);
            float h = total * (FullBody ? 1.14f : .61f) / Zoom;
            var center = new Vector3(
                (rig.Min.X + rig.Max.X) / 2,
                rig.Max.Y - h * .46f,
                (rig.Min.Z + rig.Max.Z) / 2
            );
            var vp =
                Matrix4x4.CreateLookAt(center - new Vector3(0, 0, total * 3), center, Vector3.UnitY)
                * Matrix4x4.CreateOrthographic(h * width / height, h, .001f, total * 10);
            var frame = new Scene(
                vp,
                Lights.Key.Direction,
                Lights.Key.Color,
                Lights.Fill.Direction,
                Lights.Fill.Color,
                Lights.Rim.Direction,
                Lights.Rim.Color
            );
            context.UpdateSubresource(frame, scene);
            context.UpdateSubresource(rig.Skin, boneBuffer!);
            context.UpdateSubresource(rig.EffectiveWeights, weightBuffer!);
            void DrawMaterial(Draw d, bool blendPass)
            {
                var pass = d.Surface;
                pass.Ambient = new Vector4(
                    pass.Ambient.X,
                    pass.Ambient.Y,
                    pass.Ambient.Z,
                    blendPass ? 1 : 0
                );
                context.UpdateSubresource(pass, material);
                context.PSSetShaderResource(0, d.Image);
                context.DrawIndexed((uint)d.Count, (uint)d.From, 0);
            }
            context.OMSetDepthStencilState(opaqueDepth);
            foreach (var d in draws)
                DrawMaterial(d, false);
            context.OMSetDepthStencilState(blendDepth);
            foreach (var d in draws)
                if (d.Transparent)
                    DrawMaterial(d, true);
        }
        if (present && chain != null)
            chain.Present(1, PresentFlags.None).CheckError();
    }

    public void SavePng(string path)
    {
        var d = target.Description;
        d.Usage = ResourceUsage.Staging;
        d.CPUAccessFlags = CpuAccessFlags.Read;
        d.BindFlags = BindFlags.None;
        d.MiscFlags = ResourceOptionFlags.None;
        using var copy = device.CreateTexture2D(d);
        context.CopyResource(copy, target);
        var map = context.Map(copy, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            using var img = Mat.FromPixelData(
                height,
                width,
                MatType.CV_8UC4,
                map.DataPointer,
                map.RowPitch
            );
            Cv2.ImWrite(path, img);
        }
        finally
        {
            context.Unmap(copy, 0);
        }
    }

    void DisposeModel()
    {
        Rig = null;
        draws.Clear();
        for (int i = modelResources.Count - 1; i >= 0; i--)
            modelResources[i].Dispose();
        modelResources.Clear();
    }

    public void Dispose()
    {
        context.ClearState();
        DisposeModel();
        for (int i = owned.Count - 1; i >= 0; i--)
            owned[i].Dispose();
    }
}
