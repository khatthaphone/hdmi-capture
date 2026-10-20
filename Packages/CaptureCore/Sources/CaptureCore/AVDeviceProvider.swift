import AVFoundation

extension FormatDescriptor {
    public init(_ format: AVCaptureDevice.Format) {
        let dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
        self.init(
            width: Int(dimensions.width),
            height: Int(dimensions.height),
            fourCC: FourCC.string(CMFormatDescriptionGetMediaSubType(format.formatDescription)),
            frameRateRanges: format.videoSupportedFrameRateRanges.map { FrameRateRange(min: $0.minFrameRate, max: $0.maxFrameRate) }
        )
    }
}

extension CaptureDeviceInfo {
    public init(_ device: AVCaptureDevice) {
        let transport = FourCC.string(UInt32(bitPattern: device.transportType))
        let kind: Kind
        // Continuity Camera (iPhone) also reports deviceType .external on macOS 14+.
        if device.isContinuityCamera {
            kind = .continuity
        } else {
            switch device.deviceType {
            case .external: kind = .external
            case .builtInWideAngleCamera: kind = .builtIn
            default:
                kind = switch transport {
                case "bltn": .builtIn
                case "usb ": .external
                default: .other
                }
            }
        }
        self.init(
            id: device.uniqueID,
            name: device.localizedName,
            modelID: device.modelID,
            manufacturer: device.manufacturer,
            transport: transport,
            kind: kind
        )
    }
}

/// AVFoundation-backed device discovery with hot-plug notifications.
public final class AVDeviceProvider: DeviceProviding {
    private var observers: [NSObjectProtocol] = []

    public init() {}

    deinit {
        observers.forEach(NotificationCenter.default.removeObserver)
    }

    public func videoDevices(includeNonExternal: Bool) -> [CaptureDeviceInfo] {
        var types: [AVCaptureDevice.DeviceType] = [.external]
        if includeNonExternal {
            types += [.builtInWideAngleCamera, .continuityCamera]
        }
        let devices = AVCaptureDevice.DiscoverySession(deviceTypes: types, mediaType: .video, position: .unspecified)
            .devices.map(CaptureDeviceInfo.init)
        return includeNonExternal ? devices : devices.filter { $0.kind == .external }
    }

    public func audioDevices() -> [CaptureDeviceInfo] {
        AVCaptureDevice.DiscoverySession(deviceTypes: [.microphone], mediaType: .audio, position: .unspecified)
            .devices.map(CaptureDeviceInfo.init)
    }

    public func startObserving(_ onChange: @escaping @MainActor () -> Void) {
        for name in [AVCaptureDevice.wasConnectedNotification, AVCaptureDevice.wasDisconnectedNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { onChange() }
                // A card that re-enumerates (e.g. when a source is plugged into it) can post its
                // notifications before discovery lists it again; look again shortly after.
                for delay in [1.0, 3.0] {
                    DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                        MainActor.assumeIsolated { onChange() }
                    }
                }
            })
        }
    }
}
