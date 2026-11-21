using System.Numerics;
using System.Text;
using Coocoo3D.Extensions.FileFormat;

namespace MmdLive.Rig;

// PMD 1.0 has fixed-length Shift-JIS names and refers to vertex morph targets through a base table.
internal sealed class PmdReader(BinaryReader reader)
{
    static readonly Encoding Names = GetEncoding();

    static Encoding GetEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    string Text(int width)
    {
        var bytes = reader.ReadBytes(width);
        if (bytes.Length != width)
            throw new EndOfStreamException();
        int end = Array.IndexOf(bytes, (byte)0);
        return Names.GetString(bytes, 0, end < 0 ? width : end);
    }

    Vector3 Point()
    {
        var v = Vector();
        return new(-v.X, v.Y, v.Z);
    }

    Vector3 Vector() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    int Count()
    {
        int n = reader.ReadInt32();
        if (n < 0 || n > 12000000 || n > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("无效 PMD 数量");
        return n;
    }

    int Index()
    {
        int n = reader.ReadUInt16();
        return n == 65535 ? -1 : n;
    }

    public PMXFormat Read()
    {
        if (Text(3) != "Pmd" || Math.Abs(reader.ReadSingle() - 1) > 0.01)
            throw new InvalidDataException("需要 PMD 1.0 模型");
        var result = new PMXFormat
        {
            Name = Text(20),
            NameEN = "",
            Description = Text(256),
            DescriptionEN = "",
        };
        result.Vertices = new PMX_Vertex[Count()];
        for (int i = 0; i < result.Vertices.Length; i++)
        {
            var p = Point();
            var n = Point();
            var uv = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            int a = Index(),
                b = Index();
            float weight = Math.Clamp(reader.ReadByte() / 100f, 0, 1);
            var edge = reader.ReadByte();
            result.Vertices[i] = new()
            {
                Coordinate = p,
                Normal = n,
                UvCoordinate = uv,
                boneId0 = a,
                boneId1 = b,
                boneId2 = -1,
                boneId3 = -1,
                Weights = new(weight, 1 - weight, 0, 0),
                EdgeScale = edge == 0 ? 1 : 0,
            };
        }
        result.TriangleIndexs = new int[Count()];
        for (int i = 0; i < result.TriangleIndexs.Length; i++)
            result.TriangleIndexs[i] = reader.ReadUInt16();
        int materials = Count(),
            offset = 0;
        for (int i = 0; i < materials; i++)
        {
            var diffuse = new Vector4(Vector(), reader.ReadSingle());
            var exponent = reader.ReadSingle();
            var spec = new Vector4(Vector(), exponent);
            var ambient = Vector();
            int toon = reader.ReadByte();
            reader.ReadByte();
            int indices = reader.ReadInt32();
            if (indices < 0 || indices > result.TriangleIndexs.Length - offset)
                throw new InvalidDataException("无效 PMD 材质范围");
            string texture = Text(20);
            string? main = texture
                .Split('*', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(t =>
                    !t.EndsWith(".sph", StringComparison.OrdinalIgnoreCase)
                    && !t.EndsWith(".spa", StringComparison.OrdinalIgnoreCase)
                );
            int textureIndex = -1;
            if (main != null)
            {
                textureIndex = result.Textures.FindIndex(t => t.TexturePath == main);
                if (textureIndex < 0)
                {
                    textureIndex = result.Textures.Count;
                    result.Textures.Add(new() { TexturePath = main });
                }
            }
            result.Materials.Add(
                new()
                {
                    Name = $"Material {i}",
                    NameEN = "",
                    DiffuseColor = diffuse,
                    SpecularColor = spec,
                    AmbientColor = ambient,
                    ToonIndex = toon,
                    TextureIndex = textureIndex,
                    SphereTextureIndex = -1,
                    TriangeIndexStartNum = offset,
                    TriangeIndexNum = indices,
                }
            );
            offset = checked(offset + indices);
        }
        if (offset != result.TriangleIndexs.Length)
            throw new InvalidDataException("PMD 材质索引范围有误");
        int bones = reader.ReadUInt16();
        for (int i = 0; i < bones; i++)
        {
            var name = Text(20);
            int parent = Index(),
                tail = Index();
            reader.ReadByte();
            Index();
            result.Bones.Add(
                new()
                {
                    Name = name,
                    NameEN = "",
                    ParentIndex = parent,
                    ChildId = tail,
                    Position = Point(),
                    Flags = PMX_BoneFlag.Rotatable,
                }
            );
        }
        int ik = reader.ReadUInt16();
        for (int i = 0; i < ik; i++)
        {
            Index();
            Index();
            int links = reader.ReadByte();
            reader.ReadUInt16();
            reader.ReadSingle();
            for (int j = 0; j < links; j++)
                Index();
        }
        int morphCount = reader.ReadUInt16();
        int[] baseMap = [];
        for (int i = 0; i < morphCount; i++)
        {
            string name = Text(20);
            int length = Count();
            int type = reader.ReadByte();
            if (type == 0)
            {
                baseMap = new int[length];
                for (int j = 0; j < length; j++)
                {
                    baseMap[j] = reader.ReadInt32();
                    Point();
                }
                continue;
            }
            var offsets = new PMX_MorphVertexDesc[length];
            for (int j = 0; j < length; j++)
            {
                int n = reader.ReadInt32();
                if ((uint)n >= (uint)baseMap.Length)
                    throw new InvalidDataException("PMD 表情索引超出范围");
                offsets[j] = new() { VertexIndex = baseMap[n], Offset = Point() };
            }
            result.Morphs.Add(
                new()
                {
                    Name = name,
                    NameEN = "",
                    Type = PMX_MorphType.Vertex,
                    MorphVertice = offsets,
                }
            );
        }
        result.Ready = true;
        return result;
    }
}
