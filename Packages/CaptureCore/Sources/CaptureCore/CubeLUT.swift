import Foundation

/// A colour lookup table parsed from an Adobe/Resolve `.cube` file. Always held as 3D
/// (1D tables are baked into a 3D grid) with red varying fastest, as in the file format.
public struct CubeLUT: Equatable, Sendable {
    public enum ParseError: LocalizedError, Equatable {
        case missingSize
        case unsupportedSize(Int)
        case wrongEntryCount(expected: Int, found: Int)
        case badLine(Int)

        public var errorDescription: String? {
            switch self {
            case .missingSize: "Not a .cube LUT (no LUT_3D_SIZE or LUT_1D_SIZE)."
            case let .unsupportedSize(size): "Unsupported LUT size \(size)."
            case let .wrongEntryCount(expected, found): "Expected \(expected) entries, found \(found)."
            case let .badLine(line): "Can't read line \(line)."
            }
        }
    }

    public var title: String
    public var size: Int
    public var domainMin: SIMD3<Float>
    public var domainMax: SIMD3<Float>
    /// size³ RGB entries, red fastest.
    public var entries: [SIMD3<Float>]

    public static func identity(size: Int = 17) -> CubeLUT {
        var entries: [SIMD3<Float>] = []
        entries.reserveCapacity(size * size * size)
        let scale = Float(size - 1)
        for b in 0..<size { for g in 0..<size { for r in 0..<size {
            entries.append(SIMD3(Float(r), Float(g), Float(b)) / scale)
        } } }
        return CubeLUT(title: "Identity", size: size, domainMin: .zero, domainMax: .one, entries: entries)
    }

    public init(title: String, size: Int, domainMin: SIMD3<Float>, domainMax: SIMD3<Float>, entries: [SIMD3<Float>]) {
        self.title = title
        self.size = size
        self.domainMin = domainMin
        self.domainMax = domainMax
        self.entries = entries
    }

    public init(contentsOf url: URL) throws {
        try self.init(text: String(contentsOf: url, encoding: .utf8), fallbackTitle: url.deletingPathExtension().lastPathComponent)
    }

    public init(text: String, fallbackTitle: String = "LUT") throws {
        var title = fallbackTitle
        var size3D: Int?, size1D: Int?
        var domainMin = SIMD3<Float>(0, 0, 0), domainMax = SIMD3<Float>(1, 1, 1)
        var values: [SIMD3<Float>] = []

        for (index, raw) in text.split(whereSeparator: \.isNewline).enumerated() {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty, !line.hasPrefix("#") else { continue }
            let parts = line.split(whereSeparator: { $0 == " " || $0 == "\t" })
            switch parts[0].uppercased() {
            case "TITLE":
                title = line.dropFirst(5).trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
            case "LUT_3D_SIZE":
                guard parts.count == 2, let n = Int(parts[1]) else { throw ParseError.badLine(index + 1) }
                size3D = n
            case "LUT_1D_SIZE":
                guard parts.count == 2, let n = Int(parts[1]) else { throw ParseError.badLine(index + 1) }
                size1D = n
            case "DOMAIN_MIN", "DOMAIN_MAX":
                guard let v = Self.triple(parts.dropFirst()) else { throw ParseError.badLine(index + 1) }
                if parts[0].uppercased() == "DOMAIN_MIN" { domainMin = v } else { domainMax = v }
            case "LUT_3D_INPUT_RANGE", "LUT_1D_INPUT_RANGE":
                guard parts.count == 3, let lo = Float(parts[1]), let hi = Float(parts[2]) else { throw ParseError.badLine(index + 1) }
                domainMin = SIMD3(repeating: lo)
                domainMax = SIMD3(repeating: hi)
            default:
                guard let v = Self.triple(parts[...]) else { throw ParseError.badLine(index + 1) }
                values.append(v)
            }
        }

        if let n = size3D {
            guard (2...65).contains(n) else { throw ParseError.unsupportedSize(n) }
            guard values.count == n * n * n else { throw ParseError.wrongEntryCount(expected: n * n * n, found: values.count) }
            self.init(title: title, size: n, domainMin: domainMin, domainMax: domainMax, entries: values)
        } else if let n = size1D {
            guard (2...65_536).contains(n) else { throw ParseError.unsupportedSize(n) }
            guard values.count == n else { throw ParseError.wrongEntryCount(expected: n, found: values.count) }
            self = Self.bake1D(values, title: title, domainMin: domainMin, domainMax: domainMax)
        } else {
            throw ParseError.missingSize
        }
    }

    /// Applies a per-channel 1D curve on a 33³ grid.
    private static func bake1D(_ curve: [SIMD3<Float>], title: String, domainMin: SIMD3<Float>, domainMax: SIMD3<Float>) -> CubeLUT {
        let size = 33
        func lookup(_ x: Float, _ channel: Int) -> Float {
            let position = x * Float(curve.count - 1)
            let i0 = Int(position), i1 = min(i0 + 1, curve.count - 1)
            let t = position - Float(i0)
            return curve[i0][channel] * (1 - t) + curve[i1][channel] * t
        }
        var entries: [SIMD3<Float>] = []
        entries.reserveCapacity(size * size * size)
        for b in 0..<size { for g in 0..<size { for r in 0..<size {
            let c = SIMD3(Float(r), Float(g), Float(b)) / Float(size - 1)
            entries.append(SIMD3(lookup(c.x, 0), lookup(c.y, 1), lookup(c.z, 2)))
        } } }
        return CubeLUT(title: title, size: size, domainMin: domainMin, domainMax: domainMax, entries: entries)
    }

    private static func triple(_ parts: ArraySlice<Substring>) -> SIMD3<Float>? {
        let numbers = parts.compactMap { Float($0) }
        guard parts.count == 3, numbers.count == 3 else { return nil }
        return SIMD3(numbers[0], numbers[1], numbers[2])
    }
}
