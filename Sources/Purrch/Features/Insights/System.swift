import SwiftUI
import Darwin
import IOKit
import IOKit.ps

// MARK: - Model

struct BatteryInfo: Equatable {
    var present = false
    /// 0...1
    var level: Double = 0
    var isCharging = false
    var onAC = false
    var cycleCount: Int?
    /// Max capacity / design capacity, 0...1.
    var health: Double?
    /// Minutes to empty (on battery) or to full (charging), when known.
    var minutesRemaining: Int?
}

/// Samples CPU, memory, disk, network, battery and thermal state.
/// Call `start()` / `stop()` in pairs (ref-counted); sampling runs every 1.5s while anyone is watching.
@MainActor
final class SystemMonitor: ObservableObject {
    static let shared = SystemMonitor()

    static let historyLength = 40

    /// 0...1
    @Published private(set) var cpu: Double = 0
    @Published private(set) var cpuHistory: [Double] = []
    @Published private(set) var memoryUsed: UInt64 = 0
    @Published private(set) var memoryTotal: UInt64 = ProcessInfo.processInfo.physicalMemory
    @Published private(set) var diskUsed: Int64 = 0
    @Published private(set) var diskTotal: Int64 = 0
    /// Bytes per second.
    @Published private(set) var netDown: Double = 0
    @Published private(set) var netUp: Double = 0
    @Published private(set) var netDownHistory: [Double] = []
    @Published private(set) var netUpHistory: [Double] = []
    @Published private(set) var battery = BatteryInfo()
    @Published private(set) var uptime: TimeInterval = ProcessInfo.processInfo.systemUptime
    @Published private(set) var thermalState: ProcessInfo.ThermalState = ProcessInfo.processInfo.thermalState

    var memoryFraction: Double { memoryTotal > 0 ? Double(memoryUsed) / Double(memoryTotal) : 0 }
    var diskFraction: Double { diskTotal > 0 ? Double(diskUsed) / Double(diskTotal) : 0 }

    private var users = 0
    private var timer: Timer?
    private var previousTicks: (busy: UInt64, total: UInt64)?
    private var previousNet: (down: UInt64, up: UInt64, at: Date)?
    private var sampleCount = 0

    private init() {}

    func start() {
        users += 1
        guard timer == nil else { return }
        sample()
        timer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sample() }
        }
    }

    func stop() {
        users = max(0, users - 1)
        guard users == 0 else { return }
        timer?.invalidate()
        timer = nil
        previousTicks = nil
        previousNet = nil
    }

    private func sample() {
        sampleCount += 1

        if let ticks = Self.cpuTicks() {
            if let prev = previousTicks, ticks.total > prev.total {
                let value = Double(ticks.busy &- prev.busy) / Double(ticks.total - prev.total)
                cpu = min(1, max(0, value))
                cpuHistory = Self.append(cpuHistory, cpu)
            }
            previousTicks = ticks
        }

        if let used = Self.memoryUsed() { memoryUsed = used }

        if sampleCount % 10 == 1, let disk = Self.disk() {
            diskTotal = disk.total
            diskUsed = disk.used
        }

        if let net = Self.networkBytes() {
            let now = Date()
            if let prev = previousNet {
                let dt = max(0.1, now.timeIntervalSince(prev.at))
                netDown = net.down >= prev.down ? Double(net.down - prev.down) / dt : 0
                netUp = net.up >= prev.up ? Double(net.up - prev.up) / dt : 0
                netDownHistory = Self.append(netDownHistory, netDown)
                netUpHistory = Self.append(netUpHistory, netUp)
            }
            previousNet = (net.down, net.up, now)
        }

        var info = Self.powerSource()
        if sampleCount % 20 == 1 || battery.cycleCount == nil {
            let smart = Self.smartBattery()
            info.cycleCount = smart.cycles
            info.health = smart.health
        } else {
            info.cycleCount = battery.cycleCount
            info.health = battery.health
        }
        if info != battery { battery = info }

        uptime = ProcessInfo.processInfo.systemUptime
        thermalState = ProcessInfo.processInfo.thermalState
    }

    private static func append(_ history: [Double], _ value: Double) -> [Double] {
        var h = history
        h.append(value)
        if h.count > historyLength { h.removeFirst(h.count - historyLength) }
        return h
    }

    // MARK: Probes

    private static func cpuTicks() -> (busy: UInt64, total: UInt64)? {
        var cpuCount: natural_t = 0
        var info: processor_info_array_t?
        var infoCount: mach_msg_type_number_t = 0
        let kr = host_processor_info(mach_host_self(), PROCESSOR_CPU_LOAD_INFO, &cpuCount, &info, &infoCount)
        guard kr == KERN_SUCCESS, let info else { return nil }
        defer {
            vm_deallocate(mach_task_self_, vm_address_t(bitPattern: info),
                          vm_size_t(Int(infoCount) * MemoryLayout<integer_t>.stride))
        }
        var busy: UInt64 = 0
        var total: UInt64 = 0
        for i in 0..<Int(cpuCount) {
            let base = Int(CPU_STATE_MAX) * i
            let user = UInt64(UInt32(bitPattern: info[base + Int(CPU_STATE_USER)]))
            let system = UInt64(UInt32(bitPattern: info[base + Int(CPU_STATE_SYSTEM)]))
            let nice = UInt64(UInt32(bitPattern: info[base + Int(CPU_STATE_NICE)]))
            let idle = UInt64(UInt32(bitPattern: info[base + Int(CPU_STATE_IDLE)]))
            busy += user + system + nice
            total += user + system + nice + idle
        }
        return (busy, total)
    }

    private static func memoryUsed() -> UInt64? {
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64_data_t>.stride / MemoryLayout<integer_t>.stride)
        let kr = withUnsafeMutablePointer(to: &stats) { ptr in
            ptr.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        guard kr == KERN_SUCCESS else { return nil }
        var pageSize: vm_size_t = 0
        host_page_size(mach_host_self(), &pageSize)
        let pages = UInt64(stats.active_count) + UInt64(stats.wire_count) + UInt64(stats.compressor_page_count)
        return pages * UInt64(pageSize)
    }

    private static func disk() -> (used: Int64, total: Int64)? {
        let url = URL(fileURLWithPath: "/")
        guard let values = try? url.resourceValues(forKeys: [.volumeTotalCapacityKey,
                                                             .volumeAvailableCapacityForImportantUsageKey]),
              let total = values.volumeTotalCapacity else { return nil }
        let available = values.volumeAvailableCapacityForImportantUsage ?? 0
        return (max(0, Int64(total) - available), Int64(total))
    }

    private static func networkBytes() -> (down: UInt64, up: UInt64)? {
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return nil }
        defer { freeifaddrs(ifaddr) }
        var down: UInt64 = 0
        var up: UInt64 = 0
        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let p = cursor {
            let ifa = p.pointee
            cursor = ifa.ifa_next
            guard let addr = ifa.ifa_addr, addr.pointee.sa_family == UInt8(AF_LINK),
                  (ifa.ifa_flags & UInt32(IFF_LOOPBACK)) == 0,
                  String(cString: ifa.ifa_name).hasPrefix("en"),
                  let data = ifa.ifa_data else { continue }
            let d = data.assumingMemoryBound(to: if_data.self).pointee
            down += UInt64(d.ifi_ibytes)
            up += UInt64(d.ifi_obytes)
        }
        return (down, up)
    }

    private static func powerSource() -> BatteryInfo {
        var info = BatteryInfo()
        guard let blob = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let list = IOPSCopyPowerSourcesList(blob)?.takeRetainedValue() as? [CFTypeRef] else { return info }
        for source in list {
            guard let desc = IOPSGetPowerSourceDescription(blob, source)?.takeUnretainedValue() as? [String: Any],
                  (desc[kIOPSTypeKey] as? String) == kIOPSInternalBatteryType else { continue }
            info.present = true
            let current = desc[kIOPSCurrentCapacityKey] as? Int ?? 0
            let maximum = desc[kIOPSMaxCapacityKey] as? Int ?? 100
            info.level = maximum > 0 ? Double(current) / Double(maximum) : 0
            info.isCharging = desc[kIOPSIsChargingKey] as? Bool ?? false
            info.onAC = (desc[kIOPSPowerSourceStateKey] as? String) == kIOPSACPowerValue
            let minutes = info.isCharging ? desc[kIOPSTimeToFullChargeKey] as? Int : desc[kIOPSTimeToEmptyKey] as? Int
            if let minutes, minutes > 0 { info.minutesRemaining = minutes }
            break
        }
        return info
    }

    private static func smartBattery() -> (cycles: Int?, health: Double?) {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSmartBattery"))
        guard service != 0 else { return (nil, nil) }
        defer { IOObjectRelease(service) }
        var props: Unmanaged<CFMutableDictionary>?
        guard IORegistryEntryCreateCFProperties(service, &props, kCFAllocatorDefault, 0) == KERN_SUCCESS,
              let dict = props?.takeRetainedValue() as? [String: Any] else { return (nil, nil) }
        let cycles = dict["CycleCount"] as? Int
        let design = dict["DesignCapacity"] as? Int ?? 0
        var health: Double?
        if let raw = dict["AppleRawMaxCapacity"] as? Int, design > 0 {
            health = Double(raw) / Double(design)
        } else if let maxCap = dict["MaxCapacity"] as? Int {
            if maxCap <= 100 { health = Double(maxCap) / 100 } else if design > 0 { health = Double(maxCap) / Double(design) }
        }
        return (cycles, health.map { min(1, max(0, $0)) })
    }
}

extension BatteryInfo {
    var symbolName: String {
        if isCharging { return "battery.100.bolt" }
        switch level {
        case ..<0.15: return "battery.0"
        case ..<0.4: return "battery.25"
        case ..<0.65: return "battery.50"
        case ..<0.9: return "battery.75"
        default: return "battery.100"
        }
    }
}

// MARK: - Formatting helpers

enum SystemFormat {
    static func rate(_ bytesPerSecond: Double) -> String {
        Format.bytes(Int64(bytesPerSecond)) + "/s"
    }

    static func uptime(_ t: TimeInterval) -> String {
        let total = Int(t)
        let days = total / 86400, hours = (total % 86400) / 3600, minutes = (total % 3600) / 60
        if days > 0 { return "\(days)d \(hours)h" }
        if hours > 0 { return "\(hours)h \(minutes)m" }
        return "\(minutes)m"
    }

    static func thermal(_ s: ProcessInfo.ThermalState) -> (String, Color) {
        switch s {
        case .nominal: ("Nominal", .green)
        case .fair: ("Fair", .yellow)
        case .serious: ("Serious", .orange)
        case .critical: ("Critical", .red)
        @unknown default: ("Unknown", .gray)
        }
    }

    static func gb(_ bytes: UInt64) -> String {
        String(format: "%.1f GB", Double(bytes) / 1_073_741_824)
    }
}

// MARK: - Sparkline

/// Minimal filled line chart for a short numeric history.
struct Sparkline: View {
    var values: [Double]
    var maxValue: Double? = nil
    var color: Color
    var filled = true

    var body: some View {
        GeometryReader { geo in
            let top = max(maxValue ?? (values.max() ?? 1), 0.000_001)
            let count = max(values.count, 2)
            let step = geo.size.width / CGFloat(count - 1)
            let points: [CGPoint] = values.enumerated().map { i, v in
                let x = CGFloat(i + (count - values.count)) * step
                let y = geo.size.height * (1 - CGFloat(min(1, max(0, v / top))))
                return CGPoint(x: x, y: y)
            }
            ZStack {
                if filled, let firstPoint = points.first, let lastPoint = points.last {
                    Path { p in
                        p.move(to: CGPoint(x: firstPoint.x, y: geo.size.height))
                        points.forEach { p.addLine(to: $0) }
                        p.addLine(to: CGPoint(x: lastPoint.x, y: geo.size.height))
                        p.closeSubpath()
                    }
                    .fill(LinearGradient(colors: [color.opacity(0.35), color.opacity(0.02)],
                                         startPoint: .top, endPoint: .bottom))
                }
                Path { p in
                    guard let firstPoint = points.first else { return }
                    p.move(to: firstPoint)
                    points.dropFirst().forEach { p.addLine(to: $0) }
                }
                .stroke(color, style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round))
            }
        }
    }
}

// MARK: - View

struct SystemModule: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var monitor = SystemMonitor.shared

    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 10) {
                cpuCard
                memoryDiskCard
                networkCard
            }
            HStack(spacing: 10) {
                batteryCard
                machineCard.frame(width: 230)
            }
        }
        .onAppear { monitor.start() }
        .onDisappear { monitor.stop() }
    }

    private var cpuCard: some View {
        Card("CPU", icon: "cpu", accessory: { Text("\(ProcessInfo.processInfo.activeProcessorCount) cores") }) {
            HStack(spacing: 12) {
                RingGauge(value: monitor.cpu, center: "\(Int((monitor.cpu * 100).rounded()))", label: "Load", size: 58)
                Sparkline(values: monitor.cpuHistory, maxValue: 1, color: theme.accent)
                    .frame(maxHeight: .infinity)
                    .padding(.vertical, 6)
            }
        }
    }

    private var memoryDiskCard: some View {
        Card("Memory & Disk", icon: "memorychip") {
            HStack(spacing: 18) {
                VStack(spacing: 4) {
                    RingGauge(value: monitor.memoryFraction,
                              center: "\(Int((monitor.memoryFraction * 100).rounded()))", label: "RAM", size: 58)
                    Text("\(SystemFormat.gb(monitor.memoryUsed)) / \(SystemFormat.gb(monitor.memoryTotal))")
                        .font(.system(size: 10)).foregroundStyle(theme.secondaryText).monospacedDigit()
                }
                VStack(spacing: 4) {
                    RingGauge(value: monitor.diskFraction,
                              center: "\(Int((monitor.diskFraction * 100).rounded()))", label: "Disk", size: 58)
                    Text("\(Format.bytes(monitor.diskTotal - monitor.diskUsed)) free")
                        .font(.system(size: 10)).foregroundStyle(theme.secondaryText).monospacedDigit()
                }
            }
            .frame(maxWidth: .infinity)
        }
    }

    private var networkCard: some View {
        Card("Network", icon: "network") {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 14) {
                    rateLabel(icon: "arrow.down", value: monitor.netDown, color: theme.accent)
                    rateLabel(icon: "arrow.up", value: monitor.netUp, color: theme.secondaryText)
                }
                let peak = max(monitor.netDownHistory.max() ?? 0, monitor.netUpHistory.max() ?? 0, 1024)
                ZStack {
                    Sparkline(values: monitor.netDownHistory, maxValue: peak, color: theme.accent)
                    Sparkline(values: monitor.netUpHistory, maxValue: peak, color: theme.secondaryText, filled: false)
                }
                .frame(maxHeight: .infinity)
            }
        }
    }

    private func rateLabel(icon: String, value: Double, color: Color) -> some View {
        HStack(spacing: 4) {
            Image(systemName: icon).font(.system(size: 11, weight: .bold)).foregroundStyle(color)
            Text(SystemFormat.rate(value))
                .font(.system(size: 14, weight: .semibold, design: .rounded))
                .foregroundStyle(theme.text)
                .monospacedDigit()
                .lineLimit(1)
        }
    }

    private var batteryCard: some View {
        let b = monitor.battery
        return Card("Battery", icon: b.symbolName, accessory: {
            if b.present { Text(b.isCharging ? "Charging" : (b.onAC ? "Plugged in" : "On battery")) }
        }) {
            if !b.present {
                EmptyState(icon: "powerplug", title: "No battery", subtitle: "This Mac runs on AC power")
            } else {
                HStack(alignment: .center, spacing: 20) {
                    HStack(alignment: .firstTextBaseline, spacing: 2) {
                        Text("\(Int((b.level * 100).rounded()))")
                            .font(.system(size: 42, weight: .bold, design: .rounded))
                            .foregroundStyle(theme.text)
                            .monospacedDigit()
                        Text("%").font(.system(size: 18, weight: .semibold, design: .rounded))
                            .foregroundStyle(theme.secondaryText)
                    }
                    batteryBar(b).frame(width: 120, height: 14)
                    Spacer(minLength: 0)
                    stat("Cycles", b.cycleCount.map(String.init) ?? "—")
                    stat("Health", b.health.map { "\(Int(($0 * 100).rounded()))%" } ?? "—")
                    stat(b.isCharging ? "To full" : "Remaining",
                         b.minutesRemaining.map { String(format: "%d:%02d", $0 / 60, $0 % 60) } ?? "—")
                }
                .frame(maxHeight: .infinity)
            }
        }
    }

    private func batteryBar(_ b: BatteryInfo) -> some View {
        let tint: Color = b.isCharging ? .green : (b.level < 0.2 ? .red : theme.accent)
        return GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(theme.text.opacity(0.12))
                Capsule().fill(tint).frame(width: geo.size.width * CGFloat(b.level))
                    .themeGlow(theme, radius: 4)
            }
        }
    }

    private var machineCard: some View {
        let thermal = SystemFormat.thermal(monitor.thermalState)
        return Card("Machine", icon: "desktopcomputer") {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text("Uptime").foregroundStyle(theme.secondaryText)
                    Spacer()
                    Text(SystemFormat.uptime(monitor.uptime))
                        .font(.system(size: 15, weight: .semibold, design: .rounded)).monospacedDigit()
                }
                HStack {
                    Text("Thermal").foregroundStyle(theme.secondaryText)
                    Spacer()
                    Circle().fill(thermal.1).frame(width: 7, height: 7)
                    Text(thermal.0).font(.system(size: 13, weight: .semibold))
                }
                HStack {
                    Text("macOS").foregroundStyle(theme.secondaryText)
                    Spacer()
                    let v = ProcessInfo.processInfo.operatingSystemVersion
                    Text("\(v.majorVersion).\(v.minorVersion).\(v.patchVersion)")
                        .font(.system(size: 13, weight: .semibold)).monospacedDigit()
                }
            }
            .font(.system(size: 12))
            .foregroundStyle(theme.text)
            .frame(maxHeight: .infinity, alignment: .center)
        }
    }

    private func stat(_ label: String, _ value: String) -> some View {
        VStack(spacing: 3) {
            Text(value)
                .font(.system(size: 17, weight: .semibold, design: .rounded))
                .foregroundStyle(theme.text)
                .monospacedDigit()
            Text(label.uppercased())
                .font(.system(size: 9, weight: .semibold)).tracking(0.8)
                .foregroundStyle(theme.secondaryText)
        }
        .frame(minWidth: 56)
    }

}
