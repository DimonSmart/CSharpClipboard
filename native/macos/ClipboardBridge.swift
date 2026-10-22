import AppKit
import ApplicationServices
import Carbon
import ServiceManagement

// ABI v1: cc_call takes UTF-8 JSON of explicit byte length, returns malloc-owned UTF-8 JSON.
// cc_free releases it. All calls except cc_free execute on AppKit's main thread.
// Errors are state-only codes. Snapshots own AX objects until release; no text is read from targets.
private struct Target {
    let app: NSRunningApplication
    let element: AXUIElement
    let window: AXUIElement
    var invalidated = false
}

@MainActor private final class Bridge {
    static let shared = Bridge()
    var targets: [String: Target] = [:]
    var observers: [NSObjectProtocol] = []
    var hotkey: EventHotKeyRef?
    var handler: EventHandlerRef?
    var presses = 0
    var active = true
    init() {
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { note in
            MainActor.assumeIsolated {
                guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
                      app.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return }
                for key in Array(self.targets.keys) where self.targets[key]?.app.processIdentifier != app.processIdentifier {
                    self.targets[key]?.invalidated = true
                }
            }
        })
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.sessionDidResignActiveNotification, object: nil, queue: .main) { _ in MainActor.assumeIsolated { self.active = false } })
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.sessionDidBecomeActiveNotification, object: nil, queue: .main) { _ in MainActor.assumeIsolated { self.active = true } })
    }
    func attribute(_ element: AXUIElement, _ name: String) -> AXUIElement? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success,
              let value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return (value as! AXUIElement)
    }
    func focus(_ app: NSRunningApplication) -> (AXUIElement, AXUIElement)? {
        guard active, AXIsProcessTrusted(), !app.isTerminated,
              app.bundleIdentifier != "com.apple.ScreenSharing" else { return nil }
        let application = AXUIElementCreateApplication(app.processIdentifier)
        guard let element = attribute(application, kAXFocusedUIElementAttribute),
              let window = attribute(application, kAXFocusedWindowAttribute) else { return nil }
        var subrole: CFTypeRef?
        var role: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &role) == .success,
              let role = role as? String, [kAXTextFieldRole, kAXTextAreaRole, kAXComboBoxRole].contains(role) else { return nil }
        guard AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &subrole) == .success,
              subrole as? String != kAXSecureTextFieldSubrole else { return nil }
        return (element, window)
    }
    func confirms(_ target: Target) -> Bool {
        guard !target.invalidated, NSWorkspace.shared.frontmostApplication?.processIdentifier == target.app.processIdentifier,
              let (element, window) = focus(target.app) else { return false }
        return CFEqual(element, target.element) && CFEqual(window, target.window)
    }
    func keyCode(_ wanted: String, command: Bool) -> UInt16? {
        let source = TISCopyCurrentKeyboardLayoutInputSource().takeRetainedValue()
        guard let property = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { return nil }
        let data = Unmanaged<CFData>.fromOpaque(property).takeUnretainedValue()
        guard let bytes = CFDataGetBytePtr(data) else { return nil }
        let layout = UnsafeRawPointer(bytes).assumingMemoryBound(to: UCKeyboardLayout.self)
        for key in UInt16(0)..<128 {
            var dead: UInt32 = 0
            var length = 0
            var characters = [UniChar](repeating: 0, count: 8)
            let result = UCKeyTranslate(layout, key, UInt16(kUCKeyActionDown), command ? UInt32(cmdKey >> 8) : 0,
                UInt32(LMGetKbdType()), OptionBits(kUCKeyTranslateNoDeadKeysMask), &dead, 8, &length, &characters)
            if result == noErr && length == 1 && String(utf16CodeUnits: characters, count: length).lowercased() == wanted.lowercased() { return key }
        }
        return nil
    }
    func call(_ op: String, _ args: [String: Any]) -> [String: Any] {
        let board = NSPasteboard.general
        switch op {
        case "version": return ["value": 1]
        case "path": return ["value": FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("CSharpClipboard").path]
        case "point":
            let point = NSEvent.mouseLocation
            let top = NSScreen.screens.first?.frame.maxY ?? 0
            return ["x": Int(point.x), "y": Int(top - point.y)]
        case "permission":
            let access: String
            switch board.accessBehavior {
            case .alwaysAllow: access = "Allowed"
            case .alwaysDeny: access = "Denied"
            case .ask: access = "Ask"
            default: access = "Default"
            }
            return ["value": access, "accessibility": AXIsProcessTrusted()]
        case "revision": return ["value": String(board.changeCount)]
        case "read":
            let revision = String(board.changeCount)
            guard args["revision"] as? String == revision else { return ["error": "revision_changed"] }
            guard board.accessBehavior == .alwaysAllow else { return ["revision": revision, "count": 0, "flags": 4] }
            let blocked = ["org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType", "org.nspasteboard.AutoGeneratedType", NSPasteboard.PasteboardType.fileURL.rawValue]
            let types = Set((board.types ?? []).map(\.rawValue))
            guard types.isDisjoint(with: blocked), let items = board.pasteboardItems, items.count == 1, let item = items.first else { return ["revision": revision, "count": 0, "flags": 4] }
            var result: [String: Any] = ["revision": revision, "count": 1, "flags": 0]
            if let text = item.string(forType: .string) { result["text"] = text }
            if let source = item.string(forType: NSPasteboard.PasteboardType("org.nspasteboard.source")), source.range(of: "^[A-Za-z0-9_-]+(\\.[A-Za-z0-9_-]+)+$", options: .regularExpression) != nil { result["source"] = source }
            guard String(board.changeCount) == revision else { return ["error": "revision_changed"] }
            return result
        case "write":
            guard let text = args["text"] as? String else { return ["error": "invalid_text"] }
            let item = NSPasteboardItem()
            guard item.setString(text, forType: .string) else { return ["error": "prepare_failed"] }
            item.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.AutoGeneratedType"))
            board.clearContents()
            guard board.writeObjects([item]) else { return ["error": "write_failed"] }
            return ["value": String(board.changeCount)]
        case "requestClipboard":
            if board.accessBehavior == .default || board.accessBehavior == .ask { _ = board.string(forType: .string) }
            return [:]
        case "requestAX":
            _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
            return [:]
        case "open":
            if let value = args["url"] as? String, let url = URL(string: value) { NSWorkspace.shared.open(url) }
            return [:]
        case "snapshot":
            guard let app = NSWorkspace.shared.frontmostApplication, app.processIdentifier != ProcessInfo.processInfo.processIdentifier,
                  let (element, window) = focus(app) else { return [:] }
            let id = UUID().uuidString
            targets[id] = Target(app: app, element: element, window: window)
            return ["value": id]
        case "release":
            if let id = args["id"] as? String { targets.removeValue(forKey: id) }; return [:]
        case "restore":
            guard let id = args["id"] as? String, let target = targets[id], !target.invalidated,
                  let (element, window) = focus(target.app), CFEqual(element, target.element), CFEqual(window, target.window) else { return ["value": false] }
            if let current = NSWorkspace.shared.frontmostApplication,
               current.processIdentifier != target.app.processIdentifier && current.processIdentifier != ProcessInfo.processInfo.processIdentifier { return ["value": false] }
            return ["value": target.app.activate()]
        case "dispatch":
            guard args["revision"] as? String == String(board.changeCount) else { return ["value": "ClipboardChanged"] }
            guard let id = args["id"] as? String, let target = targets[id], confirms(target),
                  CGEventSource.flagsState(.combinedSessionState).intersection([.maskControl, .maskShift, .maskAlternate, .maskCommand]).isEmpty,
                  let key = keyCode("v", command: true), let source = CGEventSource(stateID: .combinedSessionState),
                  let down = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: true),
                  let up = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false) else { return ["value": "TargetUnavailable"] }
            down.flags = .maskCommand; up.flags = .maskCommand
            guard args["revision"] as? String == String(board.changeCount) else { return ["value": "ClipboardChanged"] }
            guard confirms(target) else { return ["value": "TargetUnavailable"] }
            down.post(tap: .cghidEventTap); up.post(tap: .cghidEventTap)
            return ["value": "ShortcutSent"]
        case "startup": return ["value": SMAppService.mainApp.status == .enabled, "status": String(describing: SMAppService.mainApp.status)]
        case "setStartup":
            do { if args["enabled"] as? Bool == true { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }; return [:] }
            catch { return ["error": "startup_unavailable"] }
        case "hotkey":
            guard let key = args["key"] as? String, let modifiers = args["modifiers"] as? Int, modifiers & 11 != 0,
                  key.lowercased() != "v", let code = keyCode(key, command: modifiers & 8 != 0) else { return ["error": "reserved_shortcut"] }
            if modifiers == 8 && ["a", "c", "f", "h", "m", "n", "o", "p", "q", "s", "w", "x", "z"].contains(key.lowercased()) { return ["error": "reserved_shortcut"] }
            if handler == nil {
                var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
                let callback: EventHandlerUPP = { _, _, _ in MainActor.assumeIsolated { Bridge.shared.presses += 1 }; return noErr }
                guard InstallEventHandler(GetApplicationEventTarget(), callback, 1, &type, nil, &handler) == noErr else { return ["error": "handler_failed"] }
            }
            var carbon: UInt32 = 0
            if modifiers & 1 != 0 { carbon |= UInt32(controlKey) }
            if modifiers & 2 != 0 { carbon |= UInt32(optionKey) }
            if modifiers & 4 != 0 { carbon |= UInt32(shiftKey) }
            if modifiers & 8 != 0 { carbon |= UInt32(cmdKey) }
            var next: EventHotKeyRef?
            guard RegisterEventHotKey(UInt32(code), carbon, EventHotKeyID(signature: 0x4353434C, id: 1), GetApplicationEventTarget(), 0, &next) == noErr else { return ["error": "shortcut_unavailable"] }
            if let hotkey { UnregisterEventHotKey(hotkey) }; hotkey = next; return [:]
        case "clearHotkey": if let hotkey { UnregisterEventHotKey(hotkey) }; hotkey = nil; return [:]
        case "poll": let count = presses; presses = 0; return ["value": count]
        case "dispose":
            if let hotkey { UnregisterEventHotKey(hotkey) }; hotkey = nil
            if let handler { RemoveEventHandler(handler) }; handler = nil
            targets.removeAll(); return [:]
        default: return ["error": "unknown_operation"]
        }
    }
}

@_cdecl("cc_call") public func ccCall(_ operation: UnsafePointer<CChar>?, _ input: UnsafePointer<UInt8>?, _ length: Int32) -> UnsafeMutablePointer<CChar>? {
    guard Thread.isMainThread, let operation, length >= 0 else { return strdup("{\"error\":\"main_thread_required\"}") }
    let json: String = MainActor.assumeIsolated {
        let args = input.flatMap { try? JSONSerialization.jsonObject(with: Data(bytes: $0, count: Int(length))) as? [String: Any] } ?? [:]
        let result = Bridge.shared.call(String(cString: operation), args)
        guard let data = try? JSONSerialization.data(withJSONObject: result), let json = String(data: data, encoding: .utf8) else { return "{\"error\":\"serialization\"}" }
        return json
    }
    return strdup(json)
}
@_cdecl("cc_free") public func ccFree(_ value: UnsafeMutableRawPointer?) { free(value) }
