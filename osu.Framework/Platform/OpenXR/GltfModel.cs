// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// A static glTF 2.0 model, read from a binary glTF (.glb): its node hierarchy, triangle meshes, base colour materials and decoded
    /// images. Enough for the controller models XR runtimes provide; no GL dependency, so it can be parsed off the render thread.
    /// </summary>
    /// <remarks>
    /// Supported: nodes (matrix or TRS), triangle primitives with POSITION / NORMAL / TEXCOORD_0 and any index type, materials'
    /// base colour (factor and PNG / JPEG texture) and emissive factor, images in the binary chunk or base64 data URIs.
    /// Not supported (ignored): skins, morph targets, animations, cameras, non-triangle primitives, other textures, texture
    /// extensions (e.g. KTX2: such materials fall back to their factor).
    /// </remarks>
    internal sealed class GltfModel
    {
        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<int> RootNodes = new List<int>();
        public readonly List<Mesh> Meshes = new List<Mesh>();
        public readonly List<Material> Materials = new List<Material>();

        /// <summary>
        /// Decoded images (RGBA8, top row first), indexed by glTF image index. Null where an image couldn't be decoded.
        /// </summary>
        public readonly List<DecodedImage?> Images = new List<DecodedImage?>();

        public sealed class Node
        {
            public string Name = string.Empty;

            /// <summary>
            /// Transform relative to the parent (row-vector convention, like <see cref="System.Numerics"/>).
            /// </summary>
            public Matrix4x4 Local = Matrix4x4.Identity;

            public int Mesh = -1;
            public int[] Children = Array.Empty<int>();
        }

        public sealed class Mesh
        {
            public readonly List<Primitive> Primitives = new List<Primitive>();
        }

        public sealed class Primitive
        {
            /// <summary>
            /// Interleaved per vertex: position (3), normal (3), texture coordinate (2).
            /// </summary>
            public float[] Vertices = Array.Empty<float>();

            public uint[] Indices = Array.Empty<uint>();
            public int Material = -1;

            public const int FLOATS_PER_VERTEX = 8;
        }

        public sealed class Material
        {
            public Vector4 BaseColour = Vector4.One;
            public Vector3 Emissive = Vector3.Zero;

            /// <summary>
            /// The image index of the base colour texture, or -1.
            /// </summary>
            public int BaseColourImage = -1;

            /// <summary>
            /// Whether the material has a base colour texture that couldn't be decoded (e.g. KTX2), so its colour factor alone doesn't
            /// represent its look.
            /// </summary>
            public bool TextureUnavailable;

            public bool DoubleSided;
            public bool Blend;
        }

        public sealed record DecodedImage(int Width, int Height, byte[] Pixels);

        private const uint glb_magic = 0x46546C67; // "glTF"
        private const uint chunk_json = 0x4E4F534A; // "JSON"
        private const uint chunk_bin = 0x004E4942; // "BIN\0"

        /// <summary>
        /// Parses a binary glTF.
        /// </summary>
        /// <exception cref="FormatException">If the data isn't a supported glTF.</exception>
        public static GltfModel FromGlb(ReadOnlySpan<byte> glb)
        {
            if (glb.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != glb_magic)
                throw new FormatException("Not a binary glTF.");

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(glb[4..]);
            if (version != 2)
                throw new FormatException($"Unsupported glTF version {version}.");

            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(glb[8..]), (uint)glb.Length);

            ReadOnlySpan<byte> json = default;
            byte[] bin = Array.Empty<byte>();

            int offset = 12;

            while (offset + 8 <= length)
            {
                int chunkLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb[offset..]);
                uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(glb[(offset + 4)..]);
                var data = glb.Slice(offset + 8, Math.Min(chunkLength, length - offset - 8));

                if (chunkType == chunk_json)
                    json = data;
                else if (chunkType == chunk_bin && bin.Length == 0)
                    bin = data.ToArray();

                offset += 8 + ((chunkLength + 3) & ~3);
            }

            if (json.IsEmpty)
                throw new FormatException("glTF has no JSON chunk.");

            using var document = JsonDocument.Parse(json.ToArray());
            return new Parser(document.RootElement, bin).Parse();
        }

        /// <summary>
        /// Computes each node's transform relative to the model's root, honouring per-node overrides of the local transform and visibility.
        /// All arrays are indexed by node. Doesn't allocate.
        /// </summary>
        /// <param name="world">Receives the transforms.</param>
        /// <param name="visible">Receives whether each node (and all its ancestors) is visible.</param>
        /// <param name="localOverride">Replacement local transforms, used where <paramref name="overridden"/> is set.</param>
        /// <param name="overridden">Which nodes' local transforms are replaced.</param>
        /// <param name="hidden">Which nodes are hidden (with their descendants).</param>
        public void ComputeTransforms(Matrix4x4[] world, bool[] visible, Matrix4x4[]? localOverride = null, bool[]? overridden = null, bool[]? hidden = null)
        {
            foreach (int root in RootNodes)
                computeTransforms(root, Matrix4x4.Identity, true, world, visible, localOverride, overridden, hidden, 0);
        }

        private void computeTransforms(int index, Matrix4x4 parent, bool parentVisible, Matrix4x4[] world, bool[] visible,
                                       Matrix4x4[]? localOverride, bool[]? overridden, bool[]? hidden, int depth)
        {
            // Guards against malformed (cyclic) hierarchies.
            if (depth > 64)
                return;

            var node = Nodes[index];
            var local = overridden?[index] == true ? localOverride![index] : node.Local;
            world[index] = local * parent;
            visible[index] = parentVisible && hidden?[index] != true;

            foreach (int child in node.Children)
                computeTransforms(child, world[index], visible[index], world, visible, localOverride, overridden, hidden, depth + 1);
        }

        private sealed class Parser
        {
            private readonly JsonElement root;
            private readonly byte[] bin;
            private readonly GltfModel model = new GltfModel();

            public Parser(JsonElement root, byte[] bin)
            {
                this.root = root;
                this.bin = bin;
            }

            public GltfModel Parse()
            {
                parseImages();
                parseMaterials();
                parseMeshes();
                parseNodes();
                return model;
            }

            private void parseNodes()
            {
                if (!root.TryGetProperty("nodes", out var nodes))
                    return;

                var hasParent = new bool[nodes.GetArrayLength()];

                foreach (var n in nodes.EnumerateArray())
                {
                    var node = new Node
                    {
                        Name = n.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                        Mesh = n.TryGetProperty("mesh", out var mesh) ? mesh.GetInt32() : -1,
                        Local = readTransform(n),
                    };

                    if (n.TryGetProperty("children", out var children))
                    {
                        var list = new List<int>();

                        foreach (var c in children.EnumerateArray())
                        {
                            int child = c.GetInt32();

                            if (child >= 0 && child < hasParent.Length)
                            {
                                list.Add(child);
                                hasParent[child] = true;
                            }
                        }

                        node.Children = list.ToArray();
                    }

                    model.Nodes.Add(node);
                }

                // The default scene's roots, or else every node without a parent.
                if (root.TryGetProperty("scenes", out var scenes) && scenes.GetArrayLength() > 0)
                {
                    int sceneIndex = root.TryGetProperty("scene", out var s) ? s.GetInt32() : 0;
                    var scene = scenes[Math.Clamp(sceneIndex, 0, scenes.GetArrayLength() - 1)];

                    if (scene.TryGetProperty("nodes", out var sceneNodes))
                    {
                        foreach (var r in sceneNodes.EnumerateArray())
                            model.RootNodes.Add(r.GetInt32());
                        return;
                    }
                }

                for (int i = 0; i < hasParent.Length; i++)
                {
                    if (!hasParent[i])
                        model.RootNodes.Add(i);
                }
            }

            private static Matrix4x4 readTransform(JsonElement node)
            {
                if (node.TryGetProperty("matrix", out var matrix) && matrix.GetArrayLength() == 16)
                {
                    // glTF matrices are column-vector and column-major; read in order, that's the row-vector, row-major matrix.
                    var m = new float[16];
                    int i = 0;
                    foreach (var v in matrix.EnumerateArray())
                        m[i++] = v.GetSingle();

                    return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                }

                var scale = node.TryGetProperty("scale", out var sc) ? readVector3(sc) : Vector3.One;
                var rotation = node.TryGetProperty("rotation", out var r) ? readQuaternion(r) : Quaternion.Identity;
                var translation = node.TryGetProperty("translation", out var t) ? readVector3(t) : Vector3.Zero;

                return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            }

            private void parseMaterials()
            {
                if (!root.TryGetProperty("materials", out var materials))
                    return;

                foreach (var m in materials.EnumerateArray())
                {
                    var material = new Material();

                    if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
                    {
                        if (pbr.TryGetProperty("baseColorFactor", out var factor))
                            material.BaseColour = readVector4(factor);

                        if (pbr.TryGetProperty("baseColorTexture", out var texture) && texture.TryGetProperty("index", out var textureIndex))
                        {
                            material.BaseColourImage = imageForTexture(textureIndex.GetInt32());

                            if (material.BaseColourImage < 0 || material.BaseColourImage >= model.Images.Count || model.Images[material.BaseColourImage] == null)
                            {
                                material.BaseColourImage = -1;
                                material.TextureUnavailable = true;
                            }
                        }
                    }

                    if (m.TryGetProperty("emissiveFactor", out var emissive))
                        material.Emissive = readVector3(emissive);

                    material.DoubleSided = m.TryGetProperty("doubleSided", out var doubleSided) && doubleSided.GetBoolean();
                    material.Blend = m.TryGetProperty("alphaMode", out var alphaMode) && alphaMode.GetString() == "BLEND";

                    model.Materials.Add(material);
                }
            }

            private int imageForTexture(int textureIndex)
            {
                if (!root.TryGetProperty("textures", out var textures) || textureIndex < 0 || textureIndex >= textures.GetArrayLength())
                    return -1;

                // Textures whose only source is an extension image (e.g. KTX2 via KHR_texture_basisu) have no "source".
                var texture = textures[textureIndex];
                return texture.TryGetProperty("source", out var source) ? source.GetInt32() : -1;
            }

            private void parseImages()
            {
                if (!root.TryGetProperty("images", out var images))
                    return;

                foreach (var i in images.EnumerateArray())
                    model.Images.Add(decodeImage(i));
            }

            private DecodedImage? decodeImage(JsonElement image)
            {
                try
                {
                    byte[]? data = null;

                    if (image.TryGetProperty("bufferView", out var view))
                        data = bufferView(view.GetInt32()).ToArray();
                    else if (image.TryGetProperty("uri", out var uri) && uri.GetString() is string u && u.StartsWith("data:", StringComparison.Ordinal))
                        data = Convert.FromBase64String(u[(u.IndexOf(',') + 1)..]);

                    if (data == null)
                        return null;

                    using var decoded = Image.Load<Rgba32>(data);
                    byte[] pixels = new byte[decoded.Width * decoded.Height * 4];
                    decoded.CopyPixelDataTo(pixels);
                    return new DecodedImage(decoded.Width, decoded.Height, pixels);
                }
                catch
                {
                    // Unsupported format (e.g. KTX2): the material falls back to its colour factor.
                    return null;
                }
            }

            private void parseMeshes()
            {
                if (!root.TryGetProperty("meshes", out var meshes))
                    return;

                foreach (var m in meshes.EnumerateArray())
                {
                    var mesh = new Mesh();

                    if (m.TryGetProperty("primitives", out var primitives))
                    {
                        foreach (var p in primitives.EnumerateArray())
                        {
                            var primitive = parsePrimitive(p);
                            if (primitive != null)
                                mesh.Primitives.Add(primitive);
                        }
                    }

                    model.Meshes.Add(mesh);
                }
            }

            private Primitive? parsePrimitive(JsonElement p)
            {
                // Triangles only (mode 4, the default).
                if (p.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
                    return null;

                if (!p.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("POSITION", out var positionAccessor))
                    return null;

                float[] positions = readAccessor(positionAccessor.GetInt32(), 3);
                int vertexCount = positions.Length / 3;

                float[]? normals = attributes.TryGetProperty("NORMAL", out var n) ? readAccessor(n.GetInt32(), 3) : null;
                float[]? uvs = attributes.TryGetProperty("TEXCOORD_0", out var t) ? readAccessor(t.GetInt32(), 2) : null;

                uint[] indices;

                if (p.TryGetProperty("indices", out var indexAccessor))
                {
                    float[] raw = readAccessor(indexAccessor.GetInt32(), 1, normalise: false);
                    indices = new uint[raw.Length];
                    for (int i = 0; i < raw.Length; i++)
                        indices[i] = (uint)raw[i];
                }
                else
                {
                    indices = new uint[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                        indices[i] = (uint)i;
                }

                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] >= vertexCount)
                        throw new FormatException("glTF index out of range.");
                }

                normals ??= computeNormals(positions, indices);

                var primitive = new Primitive
                {
                    Vertices = new float[vertexCount * Primitive.FLOATS_PER_VERTEX],
                    Indices = indices,
                    Material = p.TryGetProperty("material", out var material) ? material.GetInt32() : -1,
                };

                for (int v = 0; v < vertexCount; v++)
                {
                    int o = v * Primitive.FLOATS_PER_VERTEX;
                    primitive.Vertices[o] = positions[v * 3];
                    primitive.Vertices[o + 1] = positions[v * 3 + 1];
                    primitive.Vertices[o + 2] = positions[v * 3 + 2];
                    primitive.Vertices[o + 3] = normals[v * 3];
                    primitive.Vertices[o + 4] = normals[v * 3 + 1];
                    primitive.Vertices[o + 5] = normals[v * 3 + 2];

                    if (uvs != null)
                    {
                        primitive.Vertices[o + 6] = uvs[v * 2];
                        primitive.Vertices[o + 7] = uvs[v * 2 + 1];
                    }
                }

                return primitive;
            }

            private static float[] computeNormals(float[] positions, uint[] indices)
            {
                var normals = new float[positions.Length];

                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    Vector3 a = at(indices[i]), b = at(indices[i + 1]), c = at(indices[i + 2]);
                    Vector3 face = Vector3.Cross(b - a, c - a);

                    for (int k = 0; k < 3; k++)
                    {
                        long v = indices[i + k];
                        normals[v * 3] += face.X;
                        normals[v * 3 + 1] += face.Y;
                        normals[v * 3 + 2] += face.Z;
                    }
                }

                for (int v = 0; v < normals.Length; v += 3)
                {
                    var normal = new Vector3(normals[v], normals[v + 1], normals[v + 2]);
                    normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitY;
                    (normals[v], normals[v + 1], normals[v + 2]) = (normal.X, normal.Y, normal.Z);
                }

                return normals;

                Vector3 at(uint index) => new Vector3(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);
            }

            /// <summary>
            /// Reads an accessor as floats, <paramref name="components"/> per element.
            /// </summary>
            private float[] readAccessor(int index, int components, bool normalise = true)
            {
                var accessor = root.GetProperty("accessors")[index];

                int count = accessor.GetProperty("count").GetInt32();
                int componentType = accessor.GetProperty("componentType").GetInt32();
                bool normalised = normalise && accessor.TryGetProperty("normalized", out var norm) && norm.GetBoolean();
                int available = componentCount(accessor.GetProperty("type").GetString());

                var result = new float[count * components];

                // Accessors without a buffer view are all zeros.
                if (!accessor.TryGetProperty("bufferView", out var viewIndex))
                    return result;

                var view = root.GetProperty("bufferViews")[viewIndex.GetInt32()];
                var data = bufferView(viewIndex.GetInt32());

                int componentSize = componentType switch
                {
                    5120 or 5121 => 1,
                    5122 or 5123 => 2,
                    5125 or 5126 => 4,
                    _ => throw new FormatException($"Unsupported glTF component type {componentType}."),
                };

                int elementSize = componentSize * available;
                int stride = view.TryGetProperty("byteStride", out var s) ? s.GetInt32() : elementSize;
                int offset = accessor.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;

                if (count > 0 && offset + (count - 1) * stride + elementSize > data.Length)
                    throw new FormatException("glTF accessor exceeds its buffer view.");

                for (int e = 0; e < count; e++)
                {
                    for (int c = 0; c < Math.Min(components, available); c++)
                    {
                        var bytes = data.Slice(offset + e * stride + c * componentSize, componentSize);

                        result[e * components + c] = componentType switch
                        {
                            5126 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
                            5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                            5123 => normalised ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                            5122 => normalised ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(bytes),
                            5121 => normalised ? bytes[0] / 255f : bytes[0],
                            _ => normalised ? Math.Max((sbyte)bytes[0] / 127f, -1) : (sbyte)bytes[0],
                        };
                    }
                }

                return result;
            }

            private static int componentCount(string? type) => type switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT2" => 4,
                "MAT3" => 9,
                "MAT4" => 16,
                _ => throw new FormatException($"Unsupported glTF accessor type {type}."),
            };

            private ReadOnlySpan<byte> bufferView(int index)
            {
                var view = root.GetProperty("bufferViews")[index];

                if (view.GetProperty("buffer").GetInt32() != 0)
                    throw new FormatException("Only the binary chunk buffer is supported.");

                int offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
                int length = view.GetProperty("byteLength").GetInt32();

                if (offset < 0 || length < 0 || offset + length > bin.Length)
                    throw new FormatException("glTF buffer view exceeds the binary chunk.");

                return bin.AsSpan(offset, length);
            }

            private static Vector3 readVector3(JsonElement e) => new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());

            private static Vector4 readVector4(JsonElement e) => new Vector4(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle());

            private static Quaternion readQuaternion(JsonElement e) => new Quaternion(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle());
        }

        /// <summary>
        /// A short description, for logs.
        /// </summary>
        public override string ToString()
        {
            int primitives = 0, triangles = 0;

            foreach (var mesh in Meshes)
            {
                primitives += mesh.Primitives.Count;
                foreach (var p in mesh.Primitives)
                    triangles += p.Indices.Length / 3;
            }

            var sb = new StringBuilder();
            sb.Append($"{Nodes.Count} nodes, {Meshes.Count} meshes ({primitives} primitives, {triangles} triangles), {Materials.Count} materials, ");
            sb.Append($"{Images.Count} images ({Images.FindAll(i => i != null).Count} decoded)");
            return sb.ToString();
        }
    }
}
