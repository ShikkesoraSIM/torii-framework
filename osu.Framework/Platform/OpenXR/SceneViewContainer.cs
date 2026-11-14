// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osuTK.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using RectangleF = osu.Framework.Graphics.Primitives.RectangleF;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Shows its content the way it's seen in VR, in a window: on a game panel standing in a room, with a pair of controllers
    /// pointing at it and a headset where whoever holds them looks from, seen through a camera that can be put anywhere.
    /// </summary>
    /// <remarks>
    /// <para>Drawn with the framework's own renderer, so on any graphics backend and without a VR runtime. What things look like and
    /// where they go is shared with the drawing for a headset (<see cref="OpenXRSceneRenderer"/>, <see cref="OpenXRPointerVisuals"/>):
    /// <see cref="PanelGeometry"/>, <see cref="SceneShapes"/>, <see cref="PointerBeams"/> and <see cref="GltfModel"/>.</para>
    /// <para>Everything is in one space, in metres, +Y up.</para>
    /// </remarks>
    public partial class SceneViewContainer : BufferedContainer<Drawable>
    {
        /// <summary>
        /// Where the scene is seen from.
        /// </summary>
        public Vector3 CameraPosition { get; set; }

        /// <summary>
        /// Which way the camera faces: it looks along -Z of this, with +Y up.
        /// </summary>
        public Quaternion CameraOrientation { get; set; } = Quaternion.Identity;

        /// <summary>
        /// The camera's vertical field of view, in radians.
        /// </summary>
        public float FieldOfView { get; set; } = 60 * MathF.PI / 180;

        /// <summary>
        /// Where the panel showing the content is, and its shape. Its aspect should be that of this container for the content not to be stretched.
        /// </summary>
        public PanelGeometry Panel { get; } = new PanelGeometry();

        /// <summary>
        /// The controllers (<see cref="OpenXRInput.LEFT"/>, <see cref="OpenXRInput.RIGHT"/>). One is drawn if its grip pose is valid:
        /// as a controller there, or as the <see cref="HeldModel"/> at its aim pose. With a valid aim pose it has a beam.
        /// </summary>
        public XrHandState[] Hands { get; } = new XrHandState[2];

        /// <summary>
        /// The controller driving the pointer, which is highlighted; or -1.
        /// </summary>
        public int ActiveHand { get; set; } = -1;

        private readonly SceneModel heldModel;
        private readonly SceneModel headModel;

        /// <summary>
        /// A binary glTF model drawn in place of the controllers, or null. Its origin is placed at the aim pose (-Z along the pointer ray).
        /// </summary>
        public byte[]? HeldModel
        {
            get => heldModel.Source;
            set => heldModel.Source = value;
        }

        /// <summary>
        /// A binary glTF model of a headset, drawn at <see cref="HeadPosition"/> while <see cref="ShowHead"/>; or null to draw nothing
        /// there. Its origin is the point between the eyes, and it looks along -Z with +Y up.
        /// </summary>
        public byte[]? HeadModel
        {
            get => headModel.Source;
            set => headModel.Source = value;
        }

        /// <summary>
        /// Whether the <see cref="HeadModel"/> is drawn at <see cref="HeadPosition"/>.
        /// </summary>
        public bool ShowHead { get; set; }

        /// <summary>
        /// Where the head is: the point between the eyes.
        /// </summary>
        public Vector3 HeadPosition { get; set; }

        /// <summary>
        /// Which way the head faces: it looks along -Z of this.
        /// </summary>
        public Quaternion HeadOrientation { get; set; } = Quaternion.Identity;

        /// <summary>
        /// The height of the floor, which is drawn as a grid.
        /// </summary>
        public float FloorHeight { get; set; } = -1.6f;

        /// <summary>
        /// The accent colour of the controllers (gamma-encoded RGB).
        /// </summary>
        public Vector3 Accent { get; set; } = new Vector3(1f, 0.55f, 0.2f);

        /// <summary>
        /// The colour around the scene.
        /// </summary>
        public Color4 SceneBackground { get; set; } = new Color4(0.035f, 0.035f, 0.05f, 1);

        /// <summary>
        /// The colour of the panel behind its content, where that is see-through.
        /// </summary>
        public Color4 PanelBackground { get; set; } = new Color4(0.01f, 0.01f, 0.015f, 0.9f);

        private IShader sceneShader = null!;
        private readonly SceneResources resources = new SceneResources();

        public SceneViewContainer()
        {
            RelativeSizeAxes = Axes.Both;

            heldModel = new SceneModel(this, "held");
            headModel = new SceneModel(this, "headset");
        }

        /// <summary>
        /// A model of the scene, given as a binary glTF and read in the background.
        /// </summary>
        private class SceneModel
        {
            /// <summary>
            /// The model once read, or null (there is none, it isn't read yet, or it can't be).
            /// </summary>
            public GltfModel? Model { get; private set; }

            private readonly SceneViewContainer scene;
            private readonly string name;
            private byte[]? source;

            public SceneModel(SceneViewContainer scene, string name)
            {
                this.scene = scene;
                this.name = name;
            }

            public byte[]? Source
            {
                get => source;
                set
                {
                    if (ReferenceEquals(value, source))
                        return;

                    source = value;
                    Model = null;

                    if (value == null)
                        return;

                    Task.Run(() => GltfModel.FromGlb(value)).ContinueWith(task => scene.Schedule(() =>
                    {
                        if (!ReferenceEquals(value, source))
                            return;

                        if (task.IsFaulted)
                            Logger.Log($"Couldn't read the scene's {name} model: {task.Exception?.InnerException?.Message}");
                        else
                            Model = task.GetResultSafely();
                    }));
                }
            }
        }

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders)
        {
            sceneShader = shaders.Load(VertexShaderDescriptor.TEXTURE_3, FragmentShaderDescriptor.TEXTURE);
        }

        protected override void Update()
        {
            base.Update();

            // The camera and everything in the scene can move without the content changing.
            ForceRedraw();
        }

        protected override DrawNode CreateDrawNode() => new SceneDrawNode(this, SharedData, resources);

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            resources.Dispose();
        }

        /// <summary>
        /// What the scene is drawn with, kept between frames (the draw nodes themselves take turns).
        /// </summary>
        private class SceneResources : IDisposable
        {
            public IRenderer? Renderer;
            public IFrameBuffer? Buffer;
            public IVertexBatch<TexturedVertex3D>? Batch;
            public readonly Texture?[] PointerTextures = new Texture?[Enum.GetValues<PointerImage>().Length];

            /// <summary>
            /// The models being drawn, with their textures by image index (null where an image couldn't be decoded).
            /// </summary>
            public readonly Dictionary<GltfModel, Texture?[]> Models = new Dictionary<GltfModel, Texture?[]>();

            public void Dispose() => Renderer?.ScheduleDisposal(static r =>
            {
                r.Buffer?.Dispose();
                r.Batch?.Dispose();

                foreach (var model in r.Models.Keys.ToArray())
                    r.Release(model);

                foreach (var texture in r.PointerTextures)
                    texture?.Dispose();
            }, this);

            public void Release(GltfModel model)
            {
                if (!Models.Remove(model, out var textures))
                    return;

                foreach (var texture in textures)
                    texture?.Dispose();
            }
        }

        private class SceneDrawNode : BufferedContainerDrawNode
        {
            private const float near_plane = 0.05f;
            private const float far_plane = 60f;

            private const int panel_segments = 48;

            private const float floor_extent = 8;
            private const float floor_spacing = 0.5f;
            private const float floor_line_width = 0.008f;

            protected new SceneViewContainer Source => (SceneViewContainer)base.Source;

            private readonly SceneResources resources;

            private IShader shader = null!;
            private Vector3 cameraPosition;
            private Quaternion cameraOrientation;
            private float fieldOfView;
            private readonly PanelGeometry panel = new PanelGeometry();
            private readonly XrHandState[] hands = new XrHandState[2];
            private int activeHand;
            private GltfModel? heldModel;
            private GltfModel? headModel;
            private bool showHead;
            private Vector3 headPosition;
            private Quaternion headOrientation;
            private float floorHeight;
            private Vector3 accent;
            private Color4 sceneBackground;
            private Color4 panelBackground;

            // For posing a model.
            private Matrix4x4[] nodeWorld = Array.Empty<Matrix4x4>();
            private bool[] nodeVisible = Array.Empty<bool>();

            // Whether the hands are drawn holding the held model this frame (its pointer ray comes out of its tip).
            private bool holdingModel;

            public SceneDrawNode(SceneViewContainer source, BufferedContainerDrawNodeSharedData sharedData, SceneResources resources)
                : base(source, sharedData)
            {
                this.resources = resources;
            }

            public override void ApplyState()
            {
                base.ApplyState();

                shader = Source.sceneShader;
                cameraPosition = Source.CameraPosition;
                cameraOrientation = Source.CameraOrientation;
                fieldOfView = Source.FieldOfView;

                panel.Centre = Source.Panel.Centre;
                panel.Orientation = Source.Panel.Orientation;
                panel.Radius = Source.Panel.Radius;
                panel.HorizontalAngle = Source.Panel.HorizontalAngle;
                panel.Aspect = Source.Panel.Aspect;
                panel.Cylinder = Source.Panel.Cylinder;

                hands[0] = Source.Hands[0];
                hands[1] = Source.Hands[1];
                activeHand = Source.ActiveHand;
                heldModel = Source.heldModel.Model;
                headModel = Source.headModel.Model;
                showHead = Source.ShowHead;
                headPosition = Source.HeadPosition;
                headOrientation = Source.HeadOrientation;
                floorHeight = Source.FloorHeight;
                accent = Source.Accent;
                sceneBackground = Source.SceneBackground;
                panelBackground = Source.PanelBackground;
            }

            protected override void PopulateContents(IRenderer renderer)
            {
                base.PopulateContents(renderer);
                drawScene(renderer);
            }

            protected override void DrawContents(IRenderer renderer)
            {
                // (Not drawn yet.)
                if (resources.Buffer == null)
                {
                    base.DrawContents(renderer);
                    return;
                }

                renderer.SetBlend(BlendingParameters.Mixture);
                renderer.DrawFrameBuffer(resources.Buffer, DrawRectangle, DrawColourInfo.Colour);
            }

            #region The scene

            private IRenderer renderer = null!;
            private IVertexBatch<TexturedVertex3D> batch = null!;

            // Where an untextured vertex samples: the middle of the white pixel.
            private osuTK.Vector2 whiteCoord;

            private void drawScene(IRenderer renderer)
            {
                this.renderer = renderer;
                resources.Renderer = renderer;

                var buffer = resources.Buffer ??= renderer.CreateFrameBuffer(renderBufferFormats: new[] { RenderBufferFormat.D16 });
                batch = resources.Batch ??= renderer.CreateLinearBatch<TexturedVertex3D>(3 * 4096, 32, PrimitiveTopology.Triangles);

                var content = SharedData.CurrentEffectBuffer.Texture;
                var white = renderer.WhitePixel;
                whiteCoord = white.GetTextureRect().Centre;

                using (BindFrameBuffer(buffer))
                {
                    // The scene isn't masked: positions in it aren't screen positions. The masking's scissor is worked out from the
                    // projection in use, so it's set under one which matches the buffer, to cover all of it.
                    renderer.PushOrtho(new RectangleF(0, 0, buffer.Size.X, buffer.Size.Y));
                    renderer.PushMaskingInfo(new MaskingInfo
                    {
                        ScreenSpaceAABB = new RectangleI((int)Math.Floor(DrawRectangle.X), (int)Math.Floor(DrawRectangle.Y), (int)buffer.Size.X + 1, (int)buffer.Size.Y + 1),
                        MaskingRect = new RectangleF(-1e6f, -1e6f, 2e6f, 2e6f),
                        ToMaskingSpace = osuTK.Matrix3.Identity,
                        BlendRange = 1,
                        AlphaExponent = 1,
                    }, true);
                    renderer.PopOrtho();
                    renderer.PushScissorState(false);

                    renderer.PushProjectionMatrix(createViewProjection(buffer.Size.X / Math.Max(buffer.Size.Y, 1)));
                    renderer.PushDepthInfo(new DepthInfo(true, true, BufferTestFunction.LessThan));

                    renderer.Clear(new ClearInfo(sceneBackground));

                    shader.Bind();

                    // Solid things first, writing depth; then what's seen through, back to front as far as that's known.
                    renderer.SetBlend(BlendingParameters.None);
                    white.Bind();

                    releaseUnusedModels();
                    drawHands();

                    if (showHead && prepareModel(headModel) is { } head)
                    {
                        drawModel(head, Matrix4x4.CreateFromQuaternion(headOrientation) * Matrix4x4.CreateTranslation(headPosition), SceneShapes.DEFAULT_RIM);
                        white.Bind();
                    }

                    batch.Draw();

                    renderer.PushDepthInfo(new DepthInfo(true, false, BufferTestFunction.LessThan));

                    renderer.SetBlend(BlendingParameters.Mixture);
                    drawFloor();
                    drawPanel(white, whiteCoord, whiteCoord, panelBackground, false);
                    batch.Draw();

                    renderer.PopDepthInfo();

                    // The content was blended onto nothing: its colours are already multiplied by its alpha.
                    renderer.SetBlend(premultiplied);
                    var contentRect = content.GetTextureRect();
                    drawPanel(content, contentRect.TopLeft, contentRect.BottomRight, Color4.White, true);
                    batch.Draw();

                    renderer.PushDepthInfo(new DepthInfo(true, false, BufferTestFunction.LessThan));
                    drawBeams();
                    renderer.PopDepthInfo();

                    shader.Unbind();

                    renderer.PopDepthInfo();
                    renderer.PopProjectionMatrix();
                    renderer.PopScissorState();
                    renderer.PopMaskingInfo();
                }
            }

            private static readonly BlendingParameters premultiplied = new BlendingParameters
            {
                Source = BlendingType.One,
                Destination = BlendingType.OneMinusSrcAlpha,
                SourceAlpha = BlendingType.One,
                DestinationAlpha = BlendingType.OneMinusSrcAlpha,
                RGBEquation = BlendingEquation.Add,
                AlphaEquation = BlendingEquation.Add,
            };

            /// <summary>
            /// The camera's view and perspective, in the clip space of OpenGL (the framework's shaders adapt it to other backends).
            /// </summary>
            private osuTK.Matrix4 createViewProjection(float aspect)
            {
                Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(cameraOrientation) * Matrix4x4.CreateTranslation(cameraPosition), out var view);

                float f = 1 / MathF.Tan(Math.Clamp(fieldOfView, 0.1f, 3f) / 2);

                // Row-vector convention, like the framework's own projections.
                var projection = new Matrix4x4(
                    f / aspect, 0, 0, 0,
                    0, f, 0, 0,
                    0, 0, (far_plane + near_plane) / (near_plane - far_plane), -1,
                    0, 0, 2 * far_plane * near_plane / (near_plane - far_plane), 0);

                var m = view * projection;

                return new osuTK.Matrix4(
                    m.M11, m.M12, m.M13, m.M14,
                    m.M21, m.M22, m.M23, m.M24,
                    m.M31, m.M32, m.M33, m.M34,
                    m.M41, m.M42, m.M43, m.M44);
            }

            private void drawHands()
            {
                var model = prepareModel(heldModel);
                holdingModel = model != null;

                Span<ShapePart> parts = stackalloc ShapePart[SceneShapes.MAX_PARTS];

                for (int hand = 0; hand < 2; hand++)
                {
                    ref readonly var state = ref hands[hand];

                    if (!state.GripValid)
                        continue;

                    if (model != null)
                    {
                        // The model stands in for the controller, also while it can't be placed.
                        // A faint accent outline marks the one driving the pointer, as in a headset.
                        if (state.AimValid)
                        {
                            drawModel(model, Matrix4x4.CreateFromQuaternion(state.AimOrientation) * Matrix4x4.CreateTranslation(state.AimPosition),
                                accent * (hand == activeHand ? 0.5f : 0.12f));
                        }
                    }
                    else
                        drawParts(parts[..SceneShapes.BuildController(state, hand == activeHand, accent, parts)]);
                }

                // Later shapes are untextured again.
                renderer.WhitePixel.Bind();
            }

            private void drawParts(ReadOnlySpan<ShapePart> parts)
            {
                float[] vertices = SceneShapes.Vertices;
                ushort[] indices = SceneShapes.Indices;

                foreach (ref readonly var part in parts)
                {
                    Matrix4x4.Invert(part.Model, out var inverse);
                    var normalMatrix = Matrix4x4.Transpose(inverse);

                    for (int i = part.Mesh.FirstIndex; i < part.Mesh.FirstIndex + part.Mesh.IndexCount; i++)
                    {
                        int v = indices[i] * SceneShapes.FLOATS_PER_VERTEX;

                        var position = Vector3.Transform(new Vector3(vertices[v], vertices[v + 1], vertices[v + 2]), part.Model);
                        var normal = Vector3.Normalize(Vector3.TransformNormal(new Vector3(vertices[v + 3], vertices[v + 4], vertices[v + 5]), normalMatrix));

                        add(position, lit(position, normal, part.Base, part.Emissive, SceneShapes.DEFAULT_RIM), whiteCoord);
                    }
                }
            }

            /// <summary>
            /// Lets go of the textures of models which are no longer drawn.
            /// </summary>
            private void releaseUnusedModels()
            {
                foreach (var model in resources.Models.Keys.ToArray())
                {
                    if (!ReferenceEquals(model, heldModel) && !ReferenceEquals(model, headModel))
                        resources.Release(model);
                }
            }

            /// <summary>
            /// Gets a model ready to draw: its textures are made the first time, and take a frame or so to arrive.
            /// </summary>
            /// <returns>The model, or null if there's none (or not yet).</returns>
            private GltfModel? prepareModel(GltfModel? model)
            {
                if (model == null)
                    return null;

                if (!resources.Models.TryGetValue(model, out var textures))
                {
                    resources.Models[model] = textures = new Texture?[model.Images.Count];

                    for (int i = 0; i < model.Images.Count; i++)
                    {
                        if (model.Images[i] is not { } image)
                            continue;

                        var texture = renderer.CreateTexture(image.Width, image.Height);
                        texture.SetData(new TextureUpload(Image.LoadPixelData<Rgba32>(image.Pixels, image.Width, image.Height)));
                        textures[i] = texture;
                    }
                }

                foreach (var texture in textures)
                {
                    if (texture?.Available == false)
                        return null;
                }

                return model;
            }

            /// <param name="model">A model from <see cref="prepareModel"/>.</param>
            /// <param name="root">Where it goes.</param>
            /// <param name="rim">The colour of the rim light outlining it.</param>
            private void drawModel(GltfModel model, Matrix4x4 root, Vector3 rim)
            {
                var textures = resources.Models[model];

                if (nodeWorld.Length < model.Nodes.Count)
                {
                    nodeWorld = new Matrix4x4[model.Nodes.Count];
                    nodeVisible = new bool[model.Nodes.Count];
                }

                model.ComputeTransforms(nodeWorld, nodeVisible);

                for (int n = 0; n < model.Nodes.Count; n++)
                {
                    var node = model.Nodes[n];
                    if (node.Mesh < 0 || node.Mesh >= model.Meshes.Count || !nodeVisible[n])
                        continue;

                    var world = nodeWorld[n] * root;
                    Matrix4x4.Invert(world, out var inverse);
                    var normalMatrix = Matrix4x4.Transpose(inverse);

                    foreach (var primitive in model.Meshes[node.Mesh].Primitives)
                    {
                        var material = primitive.Material >= 0 && primitive.Material < model.Materials.Count ? model.Materials[primitive.Material] : null;
                        var colour = material == null ? Vector4.One : material.TextureUnavailable ? untextured_colour : material.BaseColour;
                        var emissive = material?.Emissive ?? Vector3.Zero;
                        var texture = material?.BaseColourImage >= 0 ? textures[material.BaseColourImage] : null;

                        (texture ?? renderer.WhitePixel).Bind();
                        var rect = (texture ?? renderer.WhitePixel).GetTextureRect();

                        float[] vertices = primitive.Vertices;

                        foreach (uint index in primitive.Indices)
                        {
                            int v = (int)index * GltfModel.Primitive.FLOATS_PER_VERTEX;

                            var position = Vector3.Transform(new Vector3(vertices[v], vertices[v + 1], vertices[v + 2]), world);
                            var normal = Vector3.Normalize(Vector3.TransformNormal(new Vector3(vertices[v + 3], vertices[v + 4], vertices[v + 5]), normalMatrix));

                            // The texture's colour is multiplied by the light; without one there's a single point of white to sample.
                            var coord = texture == null
                                ? rect.Centre
                                : new osuTK.Vector2(rect.Left + vertices[v + 6] * rect.Width, rect.Top + vertices[v + 7] * rect.Height);

                            add(position, lit(position, normal, new Vector3(colour.X, colour.Y, colour.Z), emissive, rim), coord);
                        }

                        // Before the next texture is bound.
                        batch.Draw();
                    }
                }
            }

            /// <summary>
            /// The colour used for parts whose texture couldn't be decoded: neutral dark plastic.
            /// </summary>
            private static readonly Vector4 untextured_colour = new Vector4(0.12f, 0.125f, 0.135f, 1);

            private Color4 lit(Vector3 position, Vector3 normal, Vector3 baseColour, Vector3 emissive, Vector3 rim)
            {
                var toViewer = cameraPosition - position;
                toViewer = toViewer.LengthSquared() < 1e-10f ? Vector3.UnitY : Vector3.Normalize(toViewer);

                var colour = SceneShapes.Shade(normal, toViewer, baseColour, emissive, rim);
                return new Color4(MathF.Min(colour.X, 1), MathF.Min(colour.Y, 1), MathF.Min(colour.Z, 1), 1);
            }

            /// <summary>
            /// A grid of lines on the floor, fading out away from the middle of the scene.
            /// </summary>
            private void drawFloor()
            {
                // Centred under the panel's axis, where whoever is in the scene stands; snapped so the grid doesn't slide as that moves.
                float centreX = MathF.Round(panel.Centre.X / floor_spacing) * floor_spacing;
                float centreZ = MathF.Round(panel.Centre.Z / floor_spacing) * floor_spacing;
                int lines = (int)(floor_extent / floor_spacing);

                for (int i = -lines; i <= lines; i++)
                {
                    float offset = i * floor_spacing;

                    // Each line in pieces, so it can fade along its length.
                    for (int j = -lines; j < lines; j++)
                    {
                        float from = j * floor_spacing, to = from + floor_spacing;

                        floorSegment(new Vector3(centreX + offset, floorHeight, centreZ + from), new Vector3(centreX + offset, floorHeight, centreZ + to), Vector3.UnitX, centreX, centreZ);
                        floorSegment(new Vector3(centreX + from, floorHeight, centreZ + offset), new Vector3(centreX + to, floorHeight, centreZ + offset), Vector3.UnitZ, centreX, centreZ);
                    }
                }
            }

            private void floorSegment(Vector3 a, Vector3 b, Vector3 across, float centreX, float centreZ)
            {
                Color4 colourAt(Vector3 p)
                {
                    float distance = MathF.Sqrt((p.X - centreX) * (p.X - centreX) + (p.Z - centreZ) * (p.Z - centreZ));
                    float alpha = Math.Clamp(1 - distance / floor_extent, 0, 1);
                    return new Color4(0.45f, 0.5f, 0.75f, alpha * alpha * 0.55f);
                }

                var half = across * (floor_line_width / 2);
                Color4 ca = colourAt(a), cb = colourAt(b);

                if (ca.A <= 0 && cb.A <= 0)
                    return;

                quad(a - half, a + half, b + half, b - half, ca, ca, cb, cb, whiteCoord, whiteCoord, whiteCoord, whiteCoord);
            }

            /// <summary>
            /// The panel's surface: a strip of quads round its curve.
            /// </summary>
            /// <param name="texture">The texture to draw it with.</param>
            /// <param name="topLeft">The texture coordinate of the panel's top-left corner.</param>
            /// <param name="bottomRight">The texture coordinate of its bottom-right corner.</param>
            /// <param name="colour">The colour to draw it in.</param>
            /// <param name="writeDepth">Whether it's left in the depth buffer (so that beams end in it, and nothing behind it shows in front).</param>
            private void drawPanel(Texture texture, osuTK.Vector2 topLeft, osuTK.Vector2 bottomRight, Color4 colour, bool writeDepth)
            {
                batch.Draw();
                texture.Bind();

                if (writeDepth)
                    renderer.PushDepthInfo(new DepthInfo(true, true, BufferTestFunction.LessThan));

                int segments = panel.Cylinder ? panel_segments : 1;

                for (int i = 0; i < segments; i++)
                {
                    float u0 = (float)i / segments, u1 = (float)(i + 1) / segments;

                    quad(
                        panel.FromPanelSpace(panel.SurfacePointLocal(new Vector2(u0, 0))),
                        panel.FromPanelSpace(panel.SurfacePointLocal(new Vector2(u1, 0))),
                        panel.FromPanelSpace(panel.SurfacePointLocal(new Vector2(u1, 1))),
                        panel.FromPanelSpace(panel.SurfacePointLocal(new Vector2(u0, 1))),
                        colour, colour, colour, colour,
                        new osuTK.Vector2(topLeft.X + (bottomRight.X - topLeft.X) * u0, topLeft.Y),
                        new osuTK.Vector2(topLeft.X + (bottomRight.X - topLeft.X) * u1, topLeft.Y),
                        new osuTK.Vector2(topLeft.X + (bottomRight.X - topLeft.X) * u1, bottomRight.Y),
                        new osuTK.Vector2(topLeft.X + (bottomRight.X - topLeft.X) * u0, bottomRight.Y));
                }

                batch.Draw();

                if (writeDepth)
                    renderer.PopDepthInfo();
            }

            private void drawBeams()
            {
                Span<PointerQuad> quads = stackalloc PointerQuad[PointerBeams.MAX_QUADS_PER_HAND * 2];

                // Only hands which are drawn point at anything.
                Span<XrHandState> pointing = stackalloc XrHandState[2];

                for (int hand = 0; hand < 2; hand++)
                {
                    pointing[hand] = hands[hand];
                    pointing[hand].AimValid &= hands[hand].GripValid;
                }

                int count = PointerBeams.Build(pointing, activeHand, cameraPosition, panel, 0.05f, holdingModel ? 0 : 0.02f, true, true, quads);

                for (int i = 0; i < count; i++)
                {
                    ref readonly var q = ref quads[i];

                    var texture = resources.PointerTextures[(int)q.Image] ??= createPointerTexture(q.Image);

                    if (!texture.Available)
                        continue;

                    texture.Bind();
                    var rect = texture.GetTextureRect();

                    var x = Vector3.Transform(new Vector3(q.Size.X / 2, 0, 0), q.Orientation);
                    var y = Vector3.Transform(new Vector3(0, q.Size.Y / 2, 0), q.Orientation);

                    quad(q.Centre - x + y, q.Centre + x + y, q.Centre + x - y, q.Centre - x - y,
                        Color4.White, Color4.White, Color4.White, Color4.White,
                        rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft);

                    batch.Draw();
                }
            }

            private Texture createPointerTexture(PointerImage image)
            {
                byte[] pixels = PointerBeams.CreatePixels(image, out int width, out int height);

                var texture = renderer.CreateTexture(width, height);
                texture.SetData(new TextureUpload(Image.LoadPixelData<Rgba32>(pixels, width, height)));
                return texture;
            }

            private void quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color4 ca, Color4 cb, Color4 cc, Color4 cd,
                              osuTK.Vector2 ta, osuTK.Vector2 tb, osuTK.Vector2 tc, osuTK.Vector2 td)
            {
                add(a, ca, ta);
                add(b, cb, tb);
                add(c, cc, tc);
                add(a, ca, ta);
                add(c, cc, tc);
                add(d, cd, td);
            }

            private void add(Vector3 position, Color4 colour, osuTK.Vector2 textureCoord) => batch.Add(new TexturedVertex3D
            {
                Position = new osuTK.Vector3(position.X, position.Y, position.Z),
                Colour = colour,
                TexturePosition = textureCoord,
            });

            #endregion
        }
    }
}
