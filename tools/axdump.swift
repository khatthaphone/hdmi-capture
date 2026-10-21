import AppKit
let pid = NSRunningApplication.runningApplications(withBundleIdentifier: "com.example.hdmicaptain").first!.processIdentifier
let app = AXUIElementCreateApplication(pid)
func attr(_ e: AXUIElement, _ a: String) -> String? { var v: CFTypeRef?; return AXUIElementCopyAttributeValue(e, a as CFString, &v) == .success ? (v as? String) : nil }
func walk(_ e: AXUIElement, _ depth: Int) {
  let role = attr(e, kAXRoleAttribute) ?? "?"
  if ["AXButton","AXMenuButton","AXSlider","AXPopUpButton"].contains(role) {
    print(role, "| title:", attr(e, kAXTitleAttribute) ?? "-", "| desc:", attr(e, kAXDescriptionAttribute) ?? "-", "| help:", attr(e, kAXHelpAttribute) ?? "-")
  }
  var kids: CFTypeRef?; AXUIElementCopyAttributeValue(e, kAXChildrenAttribute as CFString, &kids)
  for k in (kids as? [AXUIElement]) ?? [] { walk(k, depth + 1) }
}
var wins: CFTypeRef?; AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &wins)
for w in (wins as? [AXUIElement]) ?? [] { walk(w, 0) }
