// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osuTK;
using osuTK.Graphics;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Utils;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Layout;

namespace osu.Framework.Graphics.Containers
{
    /// <summary>
    /// A container that renders its children to an internal framebuffer, and then
    /// blits the framebuffer to the screen, instead of directly rendering the children
    /// to the screen. This allows otherwise impossible effects to be applied to the
    /// appearance of the container at the cost of performance. Such effects include
    /// uniform fading of children, blur, and other post-processing effects.
    /// If all children are of a specific non-<see cref="Drawable"/> type, use the
    /// generic version <see cref="BufferedContainer{T}"/>.
    /// </summary>
    public partial class BufferedContainer : BufferedContainer<Drawable>
    {
        /// <inheritdoc />
        public BufferedContainer(TexturePixelFormat textureFormat = TexturePixelFormat.R8G8B8A8Float, RenderBufferFormat[] formats = null, bool pixelSnapping = false, bool cachedFrameBuffer = false)
            : base(textureFormat, formats, pixelSnapping, cachedFrameBuffer)
        {
        }
    }

    /// <summary>
    /// A container that renders its children to an internal framebuffer, and then
    /// blits the framebuffer to the screen, instead of directly rendering the children
    /// to the screen. This allows otherwise impossible effects to be applied to the
    /// appearance of the container at the cost of performance. Such effects include
    /// uniform fading of children, blur, and other post-processing effects.
    /// </summary>
    public partial class BufferedContainer<T> : Container<T>, IBufferedContainer, IBufferedDrawable
        where T : Drawable
    {
        private bool drawOriginal;

        /// <summary>
        /// If true the original buffered children will be drawn a second time on top of any effect (e.g. blur).
        /// </summary>
        public bool DrawOriginal
        {
            get => drawOriginal;
            set
            {
                if (drawOriginal == value)
                    return;

                drawOriginal = value;
                ForceRedraw();
            }
        }

        private Vector2 blurSigma = Vector2.Zero;

        /// <summary>
        /// Controls the amount of blurring in two orthogonal directions (X and Y if
        /// <see cref="BlurRotation"/> is zero).
        /// Blur is parametrized by a gaussian image filter. This property controls
        /// the standard deviation (sigma) of the gaussian kernel.
        /// </summary>
        public Vector2 BlurSigma
        {
            get => blurSigma;
            set
            {
                if (blurSigma == value)
                    return;

                blurSigma = value;
                ForceRedraw();
            }
        }

        private float blurRotation;

        /// <summary>
        /// Rotates the blur kernel clockwise. In degrees. Has no effect if
        /// <see cref="BlurSigma"/> has the same magnitude in both directions.
        /// </summary>
        public float BlurRotation
        {
            get => blurRotation;
            set
            {
                if (blurRotation == value)
                    return;

                blurRotation = value;
                ForceRedraw();
            }
        }

        private ColourInfo effectColour = Color4.White;

        /// <summary>
        /// The multiplicative colour of drawn buffered object after applying all effects (e.g. blur). Default is <see cref="Color4.White"/>.
        /// Does not affect the original which is drawn when <see cref="DrawOriginal"/> is true.
        /// </summary>
        public ColourInfo EffectColour
        {
            get => effectColour;
            set
            {
                if (effectColour.Equals(value))
                    return;

                effectColour = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private BlendingParameters effectBlending = BlendingParameters.Inherit;

        /// <summary>
        /// The <see cref="BlendingParameters"/> to use after applying all effects. Default is <see cref="BlendingType.Inherit"/>.
        /// <see cref="BlendingType.Inherit"/> inherits the blending mode of the original, i.e. <see cref="Drawable.Blending"/> is used.
        /// Does not affect the original which is drawn when <see cref="DrawOriginal"/> is true.
        /// </summary>
        public BlendingParameters EffectBlending
        {
            get => effectBlending;
            set
            {
                if (effectBlending == value)
                    return;

                effectBlending = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private EffectPlacement effectPlacement;

        /// <summary>
        /// Whether the buffered effect should be drawn behind or in front of the original.
        /// Behind by default. Does not have any effect if <see cref="DrawOriginal"/> is false.
        /// </summary>
        public EffectPlacement EffectPlacement
        {
            get => effectPlacement;
            set
            {
                if (effectPlacement == value)
                    return;

                effectPlacement = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private Color4 backgroundColour = new Color4(0, 0, 0, 0);

        /// <summary>
        /// The background colour of the framebuffer. Transparent black by default.
        /// </summary>
        public Color4 BackgroundColour
        {
            get => backgroundColour;
            set
            {
                if (backgroundColour == value)
                    return;

                backgroundColour = value;
                ForceRedraw();
            }
        }

        private Vector2 frameBufferScale = Vector2.One;

        public Vector2 FrameBufferScale
        {
            get => frameBufferScale;
            set
            {
                if (frameBufferScale == value)
                    return;

                frameBufferScale = value;
                ForceRedraw();
            }
        }

        private float grayscaleStrength;

        public float GrayscaleStrength
        {
            get => grayscaleStrength;
            set
            {
                if (grayscaleStrength == value)
                    return;

                grayscaleStrength = value;
                ForceRedraw();
            }
        }

        /// <summary>
        /// Whether the rendered framebuffer is being cached until <see cref="ForceRedraw"/> is called
        /// or the size of the container (i.e. framebuffer) changes.
        /// If false, then the framebuffer is re-rendered before it is blitted to the screen; equivalent
        /// to calling <see cref="ForceRedraw"/> every frame.
        /// </summary>
        public readonly bool UsingCachedFrameBuffer;

        private bool redrawOnScale = true;

        /// <summary>
        /// Whether to redraw this <see cref="BufferedContainer"/> when the draw scale changes.
        /// </summary>
        public bool RedrawOnScale
        {
            get => redrawOnScale;
            set
            {
                if (redrawOnScale == value)
                    return;

                redrawOnScale = value;
                screenSpaceSizeBacking?.Invalidate();
            }
        }

        /// <summary>
        /// Forces a redraw of the framebuffer before it is blitted the next time.
        /// Only relevant if <see cref="UsingCachedFrameBuffer"/> is true.
        /// </summary>
        public void ForceRedraw() => Invalidate(Invalidation.DrawNode);

        /// <summary>
        /// In order to signal the draw thread to re-draw the buffered container we version it.
        /// Our own version (update) keeps track of which version we are on, whereas the
        /// drawVersion keeps track of the version the draw thread is on.
        /// When forcing a redraw we increment updateVersion, pass it into each new drawnode
        /// and the draw thread will realize its drawVersion is lagging behind, thus redrawing.
        /// </summary>
        private long updateVersion;

        public IShader TextureShader { get; private set; }

        private IShader blurShader;
        private IShader grayscaleShader;

        private readonly BufferedContainerDrawNodeSharedData sharedData;

        /// <summary>
        /// The frame buffers this container draws to, for a subclass to create its own <see cref="BufferedContainerDrawNode"/> with.
        /// </summary>
        protected BufferedContainerDrawNodeSharedData SharedData => sharedData;

        /// <summary>
        /// Constructs an empty buffered container.
        /// </summary>
        /// <param name="textureFormat">The main render buffer format.</param>
        /// <param name="formats">The render buffer formats attached to the frame buffer of this <see cref="BufferedContainer"/>.</param>
        /// <param name="pixelSnapping">
        /// Whether the frame buffer position should be snapped to the nearest pixel when blitting.
        /// This amounts to setting the texture filtering mode to "nearest".
        /// </param>
        /// <param name="cachedFrameBuffer">
        /// Whether the rendered frame buffer should be cached until <see cref="ForceRedraw"/> is called
        /// or the size of the container (i.e. frame buffer) changes.
        /// When disabled, drawing will be clipped to the game window bounds. Enabling can allow drawing larger than (or outside) the game window bounds.
        /// </param>
        public BufferedContainer(TexturePixelFormat textureFormat = TexturePixelFormat.R8G8B8A8Float, RenderBufferFormat[] formats = null, bool pixelSnapping = false, bool cachedFrameBuffer = false)
        {
            UsingCachedFrameBuffer = cachedFrameBuffer;

            sharedData = new BufferedContainerDrawNodeSharedData(textureFormat, formats, pixelSnapping, !cachedFrameBuffer);

            AddLayout(screenSpaceSizeBacking);
        }

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders)
        {
            TextureShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
            blurShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.BLUR);
            grayscaleShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.GRAYSCALE);
        }

        protected override DrawNode CreateDrawNode() => new BufferedContainerDrawNode(this, sharedData);

        public override bool UpdateSubTreeMasking()
        {
            bool result = base.UpdateSubTreeMasking();

            childrenUpdateVersion = updateVersion;

            return result;
        }

        protected override RectangleF ComputeChildMaskingBounds() => ScreenSpaceDrawQuad.AABBFloat; // Make sure children never get masked away

        private Vector2 lastScreenSpaceSize;

        // We actually only care about Invalidation.MiscGeometry | Invalidation.DrawInfo
        private readonly LayoutValue screenSpaceSizeBacking = new LayoutValue(Invalidation.Presence | Invalidation.RequiredParentSizeToFit | Invalidation.DrawInfo);

        protected override bool OnInvalidate(Invalidation invalidation, InvalidationSource source)
        {
            bool result = base.OnInvalidate(invalidation, source);

            if ((invalidation & Invalidation.DrawNode) > 0)
            {
                ++updateVersion;
                result = true;
            }

            return result;
        }

        private long childrenUpdateVersion = -1;
        protected override bool RequiresChildrenUpdate => base.RequiresChildrenUpdate && !Frozen && (TrackSubtreeChanges || childrenUpdateVersion != updateVersion);

        /// <summary>
        /// Torii: with a cached frame buffer, stop updating the children and generating their draw nodes: only the cached buffer is drawn,
        /// wherever this container is. Children stay as they were (transforms, schedulers and their own updates don't run), so whoever
        /// sets this has to know nothing in here needs to change, and unset it when something does (on hover, for example).
        /// </summary>
        /// <remarks>
        /// <see cref="TrackSubtreeChanges"/> only saves the drawing; this also saves updating, laying out and generating draw nodes for
        /// everything inside, which is most of the cost of a list of cached items while it scrolls.
        /// If the buffer has to be drawn again while frozen (first draw, resized, or released by
        /// <see cref="ReleaseFrameBufferWhenNotDrawn"/>), the children's draw nodes are generated for that.
        /// </remarks>
        public bool Frozen { get; set; }

        /// <summary>
        /// Torii: the time of the last change that had the buffer drawn again, in this container's clock.
        /// With <see cref="TrackSubtreeChanges"/>, tells how long the contents have been unchanged.
        /// </summary>
        public double LastRedrawTime { get; private set; }

        private long lastRedrawVersion = -1;

        /// <summary>
        /// Torii: with a cached frame buffer, keep children updating and redraw the buffer whenever anything drawn inside it changes,
        /// at any depth.
        /// </summary>
        /// <remarks>
        /// By default a cached buffer stops updating its children and is only redrawn on invalidations it receives directly, which
        /// only come from its immediate children. That suits static content, but deeper changes (hover effects, items loading in,
        /// scrolling) are missed and anything driven by updates stalls. With this set, changes are detected while generating the
        /// children's draw nodes (a drawable whose draw state changed has its draw node re-applied, and drawables appearing or
        /// disappearing change the number of draw nodes), so caching only saves the drawing. Changes coming from outside (this container
        /// moving, scaling or fading) don't count: the buffer is drawn somewhere else, or redrawn at the new size.
        /// </remarks>
        public bool TrackSubtreeChanges { get; init; }

        /// <summary>
        /// Torii: with a cached frame buffer, free it once this container hasn't been drawn for a few seconds (for example, scrolled out of
        /// view), and draw it again when it's next drawn.
        /// </summary>
        /// <remarks>
        /// Meant for many cached items in a long list, each of which would otherwise hold on to its frame buffer for as long as it exists.
        /// </remarks>
        public bool ReleaseFrameBufferWhenNotDrawn
        {
            get => sharedData.ReleaseWhenNotDrawn;
            init => sharedData.ReleaseWhenNotDrawn = value;
        }

        private int lastSubtreeDrawNodeCount = -1;

        internal override DrawNode GenerateDrawNodeSubtree(ulong frame, int treeIndex, bool forceNewDrawNode)
        {
            if (!TrackSubtreeChanges || !UsingCachedFrameBuffer)
                return base.GenerateDrawNodeSubtree(frame, treeIndex, forceNewDrawNode);

            // Frozen and the buffer holds the current contents (as far as this thread can tell: the draw thread is the one drawing and
            // releasing it): no need for the children's draw nodes.
            if (Frozen && !forceNewDrawNode && sharedData.DrawVersion == updateVersion && GenerateOwnDrawNode(frame, treeIndex) is BufferedContainerDrawNode frozenNode)
            {
                frozenNode.ChildrenOutdated = true;
                return frozenNode;
            }

            // Changes to this container itself (being moved, say) don't change what's in the buffer: those that do invalidate its draw node.
            int changesBefore = DrawNodeContentChanges + (ContentChangedSinceLastDrawNode ? 1 : 0);
            int generatedBefore = DrawNodesGenerated;

            DrawNode node = base.GenerateDrawNodeSubtree(frame, treeIndex, forceNewDrawNode);

            int generated = DrawNodesGenerated - generatedBefore;

            if (node is BufferedContainerDrawNode bufferedNode)
            {
                bufferedNode.ChildrenOutdated = false;

                if (DrawNodeContentChanges != changesBefore || generated != lastSubtreeDrawNodeCount)
                    ++updateVersion;

                // Always, as a node only takes the version by itself when its own state is applied: every node with up-to-date children
                // then draws the buffer if it's behind (or was released), and leaves it at the current version.
                bufferedNode.ForceRedraw(updateVersion);
            }

            if (updateVersion != lastRedrawVersion)
            {
                lastRedrawVersion = updateVersion;
                LastRedrawTime = Time.Current;
            }

            lastSubtreeDrawNodeCount = generated;
            return node;
        }

        protected override void Update()
        {
            base.Update();

            // Invalidate drawn frame buffer every frame.
            if (!UsingCachedFrameBuffer)
                ForceRedraw();
            else if (!screenSpaceSizeBacking.IsValid)
            {
                Vector2 drawSize = ScreenSpaceDrawQuad.AABBFloat.Size;

                if (!RedrawOnScale)
                {
                    Matrix3 scaleMatrix = Matrix3.CreateScale(DrawInfo.MatrixInverse.ExtractScale());
                    Vector2Extensions.Transform(ref drawSize, ref scaleMatrix, out drawSize);
                }

                if (!Precision.AlmostEquals(lastScreenSpaceSize, drawSize))
                {
                    ++updateVersion;
                    lastScreenSpaceSize = drawSize;
                }

                screenSpaceSizeBacking.Validate();
            }
        }

        /// <summary>
        /// The blending which <see cref="BufferedContainerDrawNode"/> uses for the effect.
        /// </summary>
        public BlendingParameters DrawEffectBlending
        {
            get
            {
                BlendingParameters blending = EffectBlending;

                blending.CopyFromParent(Blending);
                blending.ApplyDefaultToInherited();

                return blending;
            }
        }

        /// <summary>
        /// Creates a view which can be added to a container to display the content of this <see cref="BufferedContainer{T}"/>.
        /// </summary>
        /// <returns>The view.</returns>
        public BufferedContainerView<T> CreateView() => new BufferedContainerView<T>(this, sharedData);

        public DrawColourInfo? FrameBufferDrawColour => base.DrawColourInfo;

        // Children should not receive the true colour to avoid colour doubling when the frame-buffers are rendered to the back-buffer.
        public override DrawColourInfo DrawColourInfo
        {
            get
            {
                // Todo: This is incorrect.
                var blending = Blending;
                blending.ApplyDefaultToInherited();

                return new DrawColourInfo(Color4.White, blending);
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            sharedData.Dispose();
        }
    }

    public enum EffectPlacement
    {
        Behind,
        InFront,
    }
}
