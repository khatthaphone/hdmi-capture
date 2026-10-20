import CaptureCore

/// SF Symbol per action, shared by the menu bar, the control bar and Settings so an action
/// always has the same icon wherever it appears.
enum Symbol {
    static let input = "cable.connector.horizontal"
    static let newWindow = "macwindow.badge.plus"
    static let multiView = "rectangle.grid.2x2"
    static let profiles = "person.crop.rectangle.stack"
    static let reconnect = "arrow.clockwise"

    static let fullScreen = "arrow.up.left.and.arrow.down.right"
    static let exitFullScreen = "arrow.down.right.and.arrow.up.left"
    static let screenshot = "camera"
    static let copyFrame = "doc.on.doc"
    static let mute = "speaker.slash.fill"
    static let sound = "speaker.wave.2.fill"

    static let scale = "aspectratio"
    static let pictureLook = "camera.filters"
    static let sharpPixels = "square.grid.3x3"
    static let guides = "squareshape.split.3x3"
    static let testPattern = "rectangle.split.3x1"
    static let scopes = "waveform.path.ecg"
    static let stats = "chart.bar.xaxis"

    static let floatOnTop = "pin"
    static let allSpaces = "rectangle.on.rectangle"
    static let cleanMode = "rectangle.dashed"
    static let lockAspect = "lock.rectangle"

    static let record = "record.circle"
    static let saveRecent = "gobackward"
    static let keepRecent = "clock.arrow.circlepath"
    static let goLive = "dot.radiowaves.left.and.right"
    static let virtualCamera = "web.camera"
    static let showRecordings = "folder"
    static let upgrade = "sparkles"
    static let locked = "lock.fill"
    static let options = "ellipsis.circle"
}

extension ScaleMode {
    var symbol: String {
        switch self {
        case .fit: "arrow.down.right.and.arrow.up.left.rectangle"
        case .fill: "rectangle.fill"
        case .stretch: "arrow.left.and.right.square"
        case .integer: "square.grid.3x3.square"
        }
    }
}

// Display names shared by Settings and the menus.

extension ScaleMode {
    var title: String {
        switch self {
        case .fit: "Fit"
        case .fill: "Fill (crop)"
        case .stretch: "Stretch"
        case .integer: "Integer (pixel-perfect)"
        }
    }
}

extension ChannelMode {
    var title: String {
        switch self {
        case .auto: "Automatic"
        case .normal: "Stereo"
        case .swap: "Swap left and right"
        case .leftToBoth: "Left to both"
        case .rightToBoth: "Right to both"
        case .mono: "Mono mix"
        }
    }
}
