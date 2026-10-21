import Foundation
#if os(iOS)
import Photos
#endif

/// Where screenshots and recordings go.
/// macOS: ~/Pictures and ~/Movies "HDMI Captain" folders.
/// iPadOS: the app's Documents folder (visible in Files › On My iPad › HDMI Captain), plus a copy in Photos.
enum MediaLibrary {
    static var screenshotFolder: URL {
        #if os(macOS)
        FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0].appendingPathComponent("HDMI Captain", isDirectory: true)
        #else
        documents.appendingPathComponent("Screenshots", isDirectory: true)
        #endif
    }

    static var videoFolder: URL {
        #if os(macOS)
        FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0].appendingPathComponent("HDMI Captain", isDirectory: true)
        #else
        documents.appendingPathComponent("Recordings", isDirectory: true)
        #endif
    }

    /// Where a just-saved file can be found, as a suffix for "Screenshot saved…" ("" on the Mac,
    /// where it's always the Pictures/Movies folder). On iPad this also adds the copy to Photos.
    static func destinationSuffix(for url: URL) async -> String {
        #if os(macOS)
        return ""
        #else
        return await addToPhotos(url) ? " to Photos" : " to Files › HDMI Captain"
        #endif
    }

    #if os(iOS)
    private static var documents: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
    }

    /// Adds a copy of an image or movie to the photo library (add-only access). False if not allowed.
    private static func addToPhotos(_ url: URL) async -> Bool {
        guard await PHPhotoLibrary.requestAuthorization(for: .addOnly) == .authorized else { return false }
        let isImage = url.pathExtension.lowercased() == "png"
        do {
            try await PHPhotoLibrary.shared().performChanges {
                PHAssetCreationRequest.forAsset().addResource(with: isImage ? .photo : .video, fileURL: url, options: nil)
            }
            return true
        } catch {
            return false
        }
    }
    #endif
}
