import CoreGraphics
import CoreMedia
import CoreVideo
import Metal
import os
import QuartzCore
import simd

public enum ScaleMode: String, Codable, CaseIterable, Sendable {
    case fit, fill, stretch, integer
}

public enum FilterMode: String, Codable, CaseIterable, Sendable {
    case linear, nearest
}

public enum ColorRange: String, Codable, CaseIterable, Sendable {
    case auto, limited, full
}

public struct RenderOptions: Equatable, Sendable {
    public var scale: ScaleMode = .fit
    public var filter: FilterMode = .linear
    public var colorRange: ColorRange = .auto
    public var adjustments = ImageAdjustments()
    /// 0 = LUT off, 1 = full LUT (see `MetalRenderer.setLUT`).
    public var lutIntensity: Double = 0

    public init(scale: ScaleMode = .fit, filter: FilterMode = .linear, colorRange: ColorRange = .auto,
                adjustments: ImageAdjustments = ImageAdjustments(), lutIntensity: Double = 0) {
        self.scale = scale
        self.filter = filter
        self.colorRange = colorRange
        self.adjustments = adjustments
        self.lutIntensity = lutIntensity
    }
}

/// Picture controls applied after YCbCr→RGB. Defaults are an exact identity.
public struct ImageAdjustments: Codable, Equatable, Sendable {
    /// Added to RGB, −0.5…0.5.
    public var brightness: Double = 0
    /// Around mid-grey, 0.5…1.5.
    public var contrast: Double = 1
    /// 0 (grey) … 2.
    public var saturation: Double = 1
    /// Output = input^(1/gamma), 0.5…2 (>1 brightens mid-tones).
    public var gamma: Double = 1
    /// Luma unsharp-mask amount, 0…1.
    public var sharpness: Double = 0

    public init(brightness: Double = 0, contrast: Double = 1, saturation: Double = 1, gamma: Double = 1, sharpness: Double = 0) {
        self.brightness = brightness
        self.contrast = contrast
        self.saturation = saturation
        self.gamma = gamma
        self.sharpness = sharpness
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        brightness = try c.decodeIfPresent(Double.self, forKey: .brightness) ?? 0
        contrast = try c.decodeIfPresent(Double.self, forKey: .contrast) ?? 1
        saturation = try c.decodeIfPresent(Double.self, forKey: .saturation) ?? 1
        gamma = try c.decodeIfPresent(Double.self, forKey: .gamma) ?? 1
        sharpness = try c.decodeIfPresent(Double.self, forKey: .sharpness) ?? 0
    }

    public enum Preset: String, CaseIterable, Sendable {
        case standard, game, cinema, vivid

        public var title: String {
            switch self {
            case .standard: "Standard"
            case .game: "Game"
            case .cinema: "Cinema"
            case .vivid: "Vivid"
            }
        }

        public var values: ImageAdjustments {
            switch self {
            case .standard: ImageAdjustments()
            case .game: ImageAdjustments(contrast: 1.08, saturation: 1.15, sharpness: 0.35)
            case .cinema: ImageAdjustments(brightness: -0.02, contrast: 1.05, saturation: 0.92, gamma: 0.95)
            case .vivid: ImageAdjustments(contrast: 1.15, saturation: 1.35, sharpness: 0.2)
            }
        }
    }

    /// The preset these values match, if any.
    public var preset: Preset? {
        Preset.allCases.first { $0.values == self }
    }
}

/// Where the source lands in the drawable, plus the part of the source that is visible.
public struct RenderGeometry: Equatable, Sendable {
    /// Destination rectangle in drawable pixels (always inside the drawable).
    public var viewport: CGRect
    /// Visible source region in normalized texture coordinates.
    public var sourceRect: CGRect

    public static func compute(source: CGSize, drawable: CGSize, mode: ScaleMode) -> RenderGeometry {
        let full = CGRect(origin: .zero, size: drawable)
        let unit = CGRect(x: 0, y: 0, width: 1, height: 1)
        guard source.width > 0, source.height > 0, drawable.width > 0, drawable.height > 0 else {
            return RenderGeometry(viewport: full, sourceRect: unit)
        }
        let sx = drawable.width / source.width
        let sy = drawable.height / source.height
        let scale: CGFloat
        switch mode {
        case .stretch: return RenderGeometry(viewport: full, sourceRect: unit)
        case .fit: scale = min(sx, sy)
        case .fill: scale = max(sx, sy)
        case .integer:
            let whole = floor(min(sx, sy))
            scale = whole >= 1 ? whole : min(sx, sy)
        }
        // Whole pixels, so float error can't push the origin to -1 and crop a column.
        let size = CGSize(width: (source.width * scale).rounded(), height: (source.height * scale).rounded())
        let rect = CGRect(
            x: ((drawable.width - size.width) / 2).rounded(.down),
            y: ((drawable.height - size.height) / 2).rounded(.down),
            width: size.width, height: size.height
        )
        let visible = rect.intersection(full)
        let crop = CGRect(
            x: (visible.minX - rect.minX) / rect.width,
            y: (visible.minY - rect.minY) / rect.height,
            width: visible.width / rect.width,
            height: visible.height / rect.height
        )
        return RenderGeometry(viewport: visible, sourceRect: crop)
    }
}

/// Draws captured frames straight into a CAMetalLayer from the capture queue — no main-thread
/// hop — which keeps glass-to-glass latency to roughly one display refresh.
public final class MetalRenderer: FrameSink, @unchecked Sendable {
    public let device: MTLDevice
    private let commandQueue: MTLCommandQueue
    private let yuvPipeline: MTLRenderPipelineState
    private let rgbPipeline: MTLRenderPipelineState
    private let samplers: [FilterMode: MTLSamplerState]
    private let textureCache: CVMetalTextureCache

    private let stateLock = NSLock()
    private let drawLock = NSLock()
    private var layer: CAMetalLayer?
    private var latest: CVPixelBuffer?
    /// A still (e.g. test pattern) shown instead of live frames while set.
    private var still: CVPixelBuffer?
    private var options = RenderOptions()
    private var lut: (texture: MTLTexture, size: Int, min: SIMD3<Float>, max: SIMD3<Float>)?
    /// Bound when no LUT is set, so the shader never reads an unbound texture.
    private let identityLUT: MTLTexture
    private var isVisible = true
    private var presented: UInt64 = 0
    /// Capture timestamp → arrival here (USB, decode, AVFoundation).
    private var captureLatency = LatencyTracker()
    /// Capture timestamp → drawable on screen (glass-to-glass minus the source's own lag).
    private var displayLatency = LatencyTracker()
    private let signposter = OSSignposter(subsystem: "hdmicaptain", category: "render")
    private let redrawQueue = DispatchQueue(label: "hdmicaptain.redraw", qos: .userInteractive)

    public init?() {
        guard let device = MTLCreateSystemDefaultDevice(),
              let queue = device.makeCommandQueue(),
              let library = try? device.makeLibrary(source: Self.shaderSource, options: nil)
        else { return nil }
        var cache: CVMetalTextureCache?
        CVMetalTextureCacheCreate(nil, nil, device, nil, &cache)
        guard let cache else { return nil }

        func pipeline(_ fragment: String) -> MTLRenderPipelineState? {
            let d = MTLRenderPipelineDescriptor()
            d.vertexFunction = library.makeFunction(name: "vertexQuad")
            d.fragmentFunction = library.makeFunction(name: fragment)
            d.colorAttachments[0].pixelFormat = .bgra8Unorm
            return try? device.makeRenderPipelineState(descriptor: d)
        }
        func sampler(_ filter: MTLSamplerMinMagFilter) -> MTLSamplerState? {
            let d = MTLSamplerDescriptor()
            d.minFilter = filter
            d.magFilter = filter
            d.sAddressMode = .clampToEdge
            d.tAddressMode = .clampToEdge
            return device.makeSamplerState(descriptor: d)
        }
        guard let yuv = pipeline("fragmentYCbCr"), let rgb = pipeline("fragmentRGB"),
              let linear = sampler(.linear), let nearest = sampler(.nearest),
              let identity = Self.makeLUTTexture(device: device, lut: .identity(size: 2))
        else { return nil }
        identityLUT = identity

        self.device = device
        commandQueue = queue
        yuvPipeline = yuv
        rgbPipeline = rgb
        samplers = [.linear: linear, .nearest: nearest]
        textureCache = cache
    }

    // MARK: Configuration (any thread)

    public func attach(_ layer: CAMetalLayer) {
        layer.device = device
        layer.pixelFormat = .bgra8Unorm
        layer.framebufferOnly = true
        layer.isOpaque = true
        layer.colorspace = CGColorSpace(name: CGColorSpace.itur_709)
        stateLock.withLock { self.layer = layer }
    }

    public func setOptions(_ options: RenderOptions) {
        stateLock.withLock { self.options = options }
        redraw()
    }

    /// Skip drawing while the window is fully hidden. Capture keeps running regardless.
    public func setVisible(_ visible: Bool) {
        let changed = stateLock.withLock {
            defer { isVisible = visible }
            return isVisible != visible
        }
        if changed, visible { redraw() }
    }

    public var presentedFrames: UInt64 {
        stateLock.withLock { presented }
    }

    /// Latency over the last 2 s: capture→arrival and capture→on screen.
    public func latency() -> (capture: LatencyTracker.Summary?, display: LatencyTracker.Summary?) {
        let now = CACurrentMediaTime()
        return stateLock.withLock { (captureLatency.summary(now: now), displayLatency.summary(now: now)) }
    }

    /// Pixel size of the newest frame actually delivered (can differ from the requested format).
    public var lastFrameSize: CGSize? {
        stateLock.withLock { latest.map { CGSize(width: CVPixelBufferGetWidth($0), height: CVPixelBufferGetHeight($0)) } }
    }

    /// Redraw the newest frame, e.g. after a resize or option change.
    public func redraw() {
        redrawQueue.async { [self] in
            if let frame = stateLock.withLock({ still ?? latest }) { draw(frame) }
        }
    }

    /// Sets (or clears) the colour LUT; how much of it applies is `RenderOptions.lutIntensity`.
    public func setLUT(_ lut: CubeLUT?) {
        let uploaded = lut.flatMap { lut in Self.makeLUTTexture(device: device, lut: lut).map { ($0, lut.size, lut.domainMin, lut.domainMax) } }
        stateLock.withLock { self.lut = uploaded }
        redraw()
    }

    static func makeLUTTexture(device: MTLDevice, lut: CubeLUT) -> MTLTexture? {
        let descriptor = MTLTextureDescriptor()
        descriptor.textureType = .type3D
        descriptor.pixelFormat = .rgba32Float
        descriptor.width = lut.size
        descriptor.height = lut.size
        descriptor.depth = lut.size
        descriptor.usage = .shaderRead
        guard let texture = device.makeTexture(descriptor: descriptor) else { return nil }
        var rgba = [Float]()
        rgba.reserveCapacity(lut.entries.count * 4)
        for entry in lut.entries { rgba += [entry.x, entry.y, entry.z, 1] }
        rgba.withUnsafeBytes { bytes in
            texture.replace(region: MTLRegionMake3D(0, 0, 0, lut.size, lut.size, lut.size), mipmapLevel: 0, slice: 0,
                            withBytes: bytes.baseAddress!, bytesPerRow: lut.size * 16, bytesPerImage: lut.size * lut.size * 16)
        }
        return texture
    }

    /// What is on screen right now (test pattern or newest live frame).
    public var currentFrame: CVPixelBuffer? {
        stateLock.withLock { still ?? latest }
    }

    /// Shows `buffer` instead of live frames until called with nil.
    public func showStill(_ buffer: CVPixelBuffer?) {
        stateLock.withLock { still = buffer }
        redraw()
    }

    public func clear() {
        stateLock.withLock { latest = nil }
    }

    // MARK: FrameSink

    public func consume(_ sampleBuffer: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        // Capture timestamps are on the host clock, the same clock as CACurrentMediaTime.
        let captured = CMSampleBufferGetPresentationTimeStamp(sampleBuffer).seconds
        let now = CACurrentMediaTime()
        let visible = stateLock.withLock {
            latest = pixelBuffer
            if captured.isFinite { captureLatency.add(now - captured, at: now) }
            return isVisible && still == nil
        }
        if visible { draw(pixelBuffer, capturedAt: captured.isFinite ? captured : nil) }
    }

    // MARK: Drawing

    private func draw(_ pixelBuffer: CVPixelBuffer, capturedAt: Double? = nil) {
        drawLock.lock()
        defer { drawLock.unlock() }
        let interval = signposter.beginInterval("draw")
        defer { signposter.endInterval("draw", interval) }
        let (layer, options) = stateLock.withLock { (self.layer, self.options) }
        guard let layer, layer.drawableSize.width > 0, layer.drawableSize.height > 0,
              let drawable = layer.nextDrawable(),
              let commandBuffer = commandQueue.makeCommandBuffer()
        else { return }

        let textures = encode(pixelBuffer, into: drawable.texture, options: options, commandBuffer: commandBuffer)
        #if !targetEnvironment(simulator)
        // Not available in the iOS simulator; display latency is simply not measured there.
        if let capturedAt {
            drawable.addPresentedHandler { [weak self] drawable in
                // presentedTime is 0 for some layer setups on macOS; the handler itself runs right
                // after presentation, so its time is a close (slightly late) stand-in.
                let shownAt = drawable.presentedTime > 0 ? drawable.presentedTime : CACurrentMediaTime()
                guard let self else { return }
                self.stateLock.withLock { self.displayLatency.add(shownAt - capturedAt, at: shownAt) }
            }
        }
        #endif
        commandBuffer.present(drawable)
        commandBuffer.addCompletedHandler { [weak self] _ in
            _ = textures // keep CVMetalTextures alive until the GPU is done
            self?.stateLock.withLock { self?.presented += 1 }
        }
        commandBuffer.commit()
    }

    /// Encodes one frame into `target`. Returns the CVMetalTextures that must outlive the command buffer.
    @discardableResult
    public func encode(_ pixelBuffer: CVPixelBuffer, into target: MTLTexture, options: RenderOptions, commandBuffer: MTLCommandBuffer) -> [CVMetalTexture] {
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = target
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 1)
        pass.colorAttachments[0].storeAction = .store
        guard let encoder = commandBuffer.makeRenderCommandEncoder(descriptor: pass) else { return [] }
        defer { encoder.endEncoding() }

        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        let geometry = RenderGeometry.compute(
            source: CGSize(width: width, height: height),
            drawable: CGSize(width: target.width, height: target.height),
            mode: options.scale
        )
        guard geometry.viewport.width >= 1, geometry.viewport.height >= 1 else { return [] }
        encoder.setViewport(MTLViewport(
            originX: geometry.viewport.minX, originY: geometry.viewport.minY,
            width: geometry.viewport.width, height: geometry.viewport.height, znear: 0, zfar: 1
        ))
        var uvRect = SIMD4<Float>(
            Float(geometry.sourceRect.minX), Float(geometry.sourceRect.minY),
            Float(geometry.sourceRect.maxX), Float(geometry.sourceRect.maxY)
        )
        encoder.setVertexBytes(&uvRect, length: MemoryLayout<SIMD4<Float>>.size, index: 0)
        encoder.setFragmentSamplerState(samplers[options.filter], index: 0)
        let lut = stateLock.withLock { self.lut }
        let lutTexture = lut?.texture ?? identityLUT
        let lutParams = (intensity: lut == nil ? 0 : Float(options.lutIntensity), size: Float(lut?.size ?? 2),
                         min: lut?.min ?? .zero, max: lut?.max ?? .one)

        let pixelFormat = CVPixelBufferGetPixelFormatType(pixelBuffer)
        switch pixelFormat {
        case kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange, kCVPixelFormatType_420YpCbCr8BiPlanarFullRange:
            guard let luma = texture(pixelBuffer, plane: 0, format: .r8Unorm),
                  let chroma = texture(pixelBuffer, plane: 1, format: .rg8Unorm)
            else { return [] }
            var params = YCbCrParams.make(
                fullRange: Self.isFullRange(pixelFormat: pixelFormat, override: options.colorRange),
                bt709: Self.isBT709(pixelBuffer, height: height),
                adjustments: options.adjustments,
                texel: SIMD2(1 / Float(width), 1 / Float(height))
            )
            params.setLUT(lutParams)
            encoder.setFragmentTexture(lutTexture, index: 2)
            encoder.setRenderPipelineState(yuvPipeline)
            encoder.setFragmentTexture(CVMetalTextureGetTexture(luma), index: 0)
            encoder.setFragmentTexture(CVMetalTextureGetTexture(chroma), index: 1)
            encoder.setFragmentBytes(&params, length: MemoryLayout<YCbCrParams>.stride, index: 0)
            encoder.drawPrimitives(type: .triangleStrip, vertexStart: 0, vertexCount: 4)
            return [luma, chroma]
        case kCVPixelFormatType_32BGRA:
            guard let rgb = texture(pixelBuffer, plane: 0, format: .bgra8Unorm) else { return [] }
            var params = YCbCrParams.make(fullRange: true, bt709: true, adjustments: options.adjustments, texel: .zero)
            params.setLUT(lutParams)
            encoder.setFragmentTexture(lutTexture, index: 2)
            encoder.setRenderPipelineState(rgbPipeline)
            encoder.setFragmentTexture(CVMetalTextureGetTexture(rgb), index: 0)
            encoder.setFragmentBytes(&params, length: MemoryLayout<YCbCrParams>.stride, index: 0)
            encoder.drawPrimitives(type: .triangleStrip, vertexStart: 0, vertexCount: 4)
            return [rgb]
        default:
            return []
        }
    }

    private func texture(_ buffer: CVPixelBuffer, plane: Int, format: MTLPixelFormat) -> CVMetalTexture? {
        let planar = CVPixelBufferIsPlanar(buffer)
        let width = planar ? CVPixelBufferGetWidthOfPlane(buffer, plane) : CVPixelBufferGetWidth(buffer)
        let height = planar ? CVPixelBufferGetHeightOfPlane(buffer, plane) : CVPixelBufferGetHeight(buffer)
        var texture: CVMetalTexture?
        CVMetalTextureCacheCreateTextureFromImage(nil, textureCache, buffer, nil, format, width, height, plane, &texture)
        return texture
    }

    static func isFullRange(pixelFormat: OSType, override: ColorRange) -> Bool {
        switch override {
        case .full: true
        case .limited: false
        case .auto: pixelFormat == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
        }
    }

    static func isBT709(_ buffer: CVPixelBuffer, height: Int) -> Bool {
        if let matrix = CVBufferCopyAttachment(buffer, kCVImageBufferYCbCrMatrixKey, nil) as? String {
            return matrix != (kCVImageBufferYCbCrMatrix_ITU_R_601_4 as String)
        }
        return height >= 720
    }

    static let shaderSource = """
    #include <metal_stdlib>
    using namespace metal;

    struct VertexOut {
        float4 position [[position]];
        float2 uv;
    };

    struct YCbCrParams {
        float3x3 matrix;
        float3 offset;
        float3 scale;
        float4 adjust;   // brightness, contrast, saturation, 1/gamma
        float4 sharpen;  // amount, texel x, texel y, unused
        float4 lut;      // intensity, size, unused, unused
        float4 lutMin;   // domain min (xyz)
        float4 lutMax;   // domain max (xyz)
    };

    static float3 applyLUT(float3 rgb, texture3d<float> table, constant YCbCrParams &p) {
        if (p.lut.x <= 0) { return rgb; }
        constexpr sampler linear(filter::linear, address::clamp_to_edge);
        float n = p.lut.y;
        float3 c = saturate((rgb - p.lutMin.xyz) / (p.lutMax.xyz - p.lutMin.xyz));
        float3 graded = table.sample(linear, c * ((n - 1) / n) + 0.5 / n).rgb;
        return mix(rgb, saturate(graded), p.lut.x);
    }

    // Identity when adjust == (0, 1, 1, 1).
    static float3 adjustColor(float3 rgb, float4 a) {
        rgb = (rgb - 0.5) * a.y + 0.5 + a.x;
        float luma = dot(rgb, float3(0.2126, 0.7152, 0.0722));
        rgb = mix(float3(luma), rgb, a.z);
        rgb = saturate(rgb);
        return a.w == 1.0 ? rgb : pow(rgb, float3(a.w));
    }

    vertex VertexOut vertexQuad(uint vid [[vertex_id]], constant float4 &uvRect [[buffer(0)]]) {
        const float2 corners[4] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(1, 1) };
        float2 c = corners[vid];
        VertexOut out;
        out.position = float4(c.x * 2 - 1, 1 - c.y * 2, 0, 1);
        out.uv = mix(uvRect.xy, uvRect.zw, c);
        return out;
    }

    fragment float4 fragmentYCbCr(VertexOut in [[stage_in]],
                                  texture2d<float> luma [[texture(0)]],
                                  texture2d<float> chroma [[texture(1)]],
                                  texture3d<float> table [[texture(2)]],
                                  sampler s [[sampler(0)]],
                                  constant YCbCrParams &p [[buffer(0)]]) {
        float y = luma.sample(s, in.uv).r;
        if (p.sharpen.x > 0) {
            float2 t = p.sharpen.yz;
            float neighbours = luma.sample(s, in.uv + float2(t.x, 0)).r + luma.sample(s, in.uv - float2(t.x, 0)).r
                             + luma.sample(s, in.uv + float2(0, t.y)).r + luma.sample(s, in.uv - float2(0, t.y)).r;
            y += (y - neighbours * 0.25) * p.sharpen.x * 2.0;
        }
        float3 ycc = float3(y, chroma.sample(s, in.uv).rg);
        float3 rgb = saturate(p.matrix * ((ycc - p.offset) * p.scale));
        return float4(applyLUT(adjustColor(rgb, p.adjust), table, p), 1);
    }

    fragment float4 fragmentRGB(VertexOut in [[stage_in]],
                                texture2d<float> image [[texture(0)]],
                                texture3d<float> table [[texture(2)]],
                                sampler s [[sampler(0)]],
                                constant YCbCrParams &p [[buffer(0)]]) {
        return float4(applyLUT(adjustColor(image.sample(s, in.uv).rgb, p.adjust), table, p), 1);
    }
    """
}

/// Mirrors `YCbCrParams` in the shader (float3x3, 2 × float3, 2 × float4; all 16-byte aligned).
struct YCbCrParams {
    var matrix: simd_float3x3
    var offset: SIMD3<Float>
    var scale: SIMD3<Float>
    var adjust = SIMD4<Float>(0, 1, 1, 1)
    var sharpen = SIMD4<Float>.zero
    var lut = SIMD4<Float>.zero
    var lutMin = SIMD4<Float>.zero
    var lutMax = SIMD4<Float>(1, 1, 1, 0)

    mutating func setLUT(_ value: (intensity: Float, size: Float, min: SIMD3<Float>, max: SIMD3<Float>)) {
        lut = SIMD4(value.intensity, value.size, 0, 0)
        lutMin = SIMD4(value.min, 0)
        lutMax = SIMD4(value.max, 0)
    }

    static func make(fullRange: Bool, bt709: Bool, adjustments: ImageAdjustments, texel: SIMD2<Float>) -> YCbCrParams {
        var params = make(fullRange: fullRange, bt709: bt709)
        let a = adjustments
        params.adjust = SIMD4(Float(a.brightness), Float(a.contrast), Float(a.saturation), Float(1 / max(0.1, a.gamma)))
        params.sharpen = SIMD4(Float(a.sharpness), texel.x, texel.y, 0)
        return params
    }

    static func make(fullRange: Bool, bt709: Bool) -> YCbCrParams {
        // Columns are the Y, Cb, Cr contributions to (R, G, B).
        let matrix = bt709
            ? simd_float3x3(columns: (SIMD3(1, 1, 1), SIMD3(0, -0.1873, 1.8556), SIMD3(1.5748, -0.4681, 0)))
            : simd_float3x3(columns: (SIMD3(1, 1, 1), SIMD3(0, -0.344136, 1.772), SIMD3(1.402, -0.714136, 0)))
        let chromaOffset: Float = 128 / 255
        return fullRange
            ? YCbCrParams(matrix: matrix, offset: SIMD3(0, chromaOffset, chromaOffset), scale: SIMD3(1, 1, 1))
            : YCbCrParams(matrix: matrix, offset: SIMD3(16 / 255, chromaOffset, chromaOffset), scale: SIMD3(255 / 219, 255 / 224, 255 / 224))
    }
}
