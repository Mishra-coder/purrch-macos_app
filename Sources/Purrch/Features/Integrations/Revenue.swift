import SwiftUI

struct StripeSale: Identifiable {
    let id: String
    let amount: Double
    let label: String
    let created: Date
}

@MainActor
final class RevenueService: ObservableObject {
    static let shared = RevenueService()

    @Published private(set) var todayTotal: Double = 0
    @Published private(set) var todaySales: [StripeSale] = []
    @Published private(set) var week: [(day: Date, total: Double)] = []
    @Published private(set) var error: String?
    @Published private(set) var lastUpdated: Date?
    @Published private(set) var isLoading = false

    private static let zeroDecimal: Set<String> = ["bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga",
                                                   "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf"]

    func refresh() async {
        let settings = AppSettings.shared
        let key = settings.stripeKey
        let currency = settings.revenueCurrency.lowercased()
        guard !key.isEmpty, !isLoading else { return }
        isLoading = true
        defer { isLoading = false }

        let calendar = Calendar.current
        let today = calendar.startOfDay(for: Date())
        let weekStart = calendar.date(byAdding: .day, value: -6, to: today)!
        do {
            // Newest first, so today's charges always arrive in the first page.
            let charges = try await fetchCharges(since: weekStart, key: key)
            let divisor = Self.zeroDecimal.contains(currency) ? 1.0 : 100.0
            var sales: [StripeSale] = []
            for charge in charges {
                guard charge["status"] as? String == "succeeded",
                      charge["paid"] as? Bool ?? true,
                      charge["refunded"] as? Bool != true,
                      (charge["currency"] as? String)?.lowercased() == currency else { continue }
                let amount = (charge["amount"] as? NSNumber)?.doubleValue ?? 0
                let refunded = (charge["amount_refunded"] as? NSNumber)?.doubleValue ?? 0
                let created = Date(timeIntervalSince1970: (charge["created"] as? NSNumber)?.doubleValue ?? 0)
                let billing = charge["billing_details"] as? [String: Any]
                let label = [billing?["email"] as? String, charge["receipt_email"] as? String,
                             charge["description"] as? String, billing?["name"] as? String]
                    .compactMap { $0 }.first { !$0.isEmpty } ?? "Payment"
                sales.append(StripeSale(id: charge["id"] as? String ?? UUID().uuidString,
                                        amount: (amount - refunded) / divisor, label: label, created: created))
            }
            let todays = sales.filter { $0.created >= today }
            todaySales = todays
            todayTotal = todays.reduce(0) { $0 + $1.amount }
            week = (0..<7).map { offset in
                let day = calendar.date(byAdding: .day, value: offset, to: weekStart)!
                let next = calendar.date(byAdding: .day, value: 1, to: day)!
                return (day, sales.filter { $0.created >= day && $0.created < next }.reduce(0) { $0 + $1.amount })
            }
            error = nil
            lastUpdated = Date()
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func fetchCharges(since: Date, key: String) async throws -> [[String: Any]] {
        var all: [[String: Any]] = []
        var startingAfter: String?
        for _ in 0..<5 {
            var components = URLComponents(string: "https://api.stripe.com/v1/charges")!
            var items = [URLQueryItem(name: "created[gte]", value: String(Int(since.timeIntervalSince1970))),
                         URLQueryItem(name: "limit", value: "100")]
            if let startingAfter { items.append(URLQueryItem(name: "starting_after", value: startingAfter)) }
            components.queryItems = items
            let json = try await HTTPJSON.get(components.url!, bearer: key) as? [String: Any]
            let page = json?["data"] as? [[String: Any]] ?? []
            all += page
            guard json?["has_more"] as? Bool == true, let last = page.last?["id"] as? String else { break }
            startingAfter = last
        }
        return all
    }
}

struct RevenueModule: View {
    @Environment(\.theme) private var theme
    @StateObject private var service = RevenueService.shared
    @ObservedObject private var settings = AppSettings.shared

    private var currencyCode: String { settings.revenueCurrency.uppercased() }

    var body: some View {
        Group {
            if settings.stripeKey.isEmpty {
                MissingSettingView(icon: "creditcard", title: "Connect Stripe",
                                   subtitle: "Add a restricted, read-only Stripe key (Charges: read) in Settings.")
            } else {
                HStack(spacing: 10) {
                    VStack(spacing: 10) {
                        totalCard
                        weekCard
                    }
                    .frame(width: 300)
                    salesCard
                }
            }
        }
        .task(id: settings.stripeKey) {
            while !Task.isCancelled {
                await service.refresh()
                try? await Task.sleep(for: .seconds(60))
            }
        }
    }

    private var accent: Color { theme.accent(for: .revenue) }

    private func money(_ value: Double) -> String {
        value.formatted(.currency(code: currencyCode).precision(.fractionLength(value.rounded() == value ? 0...0 : 2...2)))
    }

    private var totalCard: some View {
        Card("Today", icon: "creditcard", accessory: {
            if service.isLoading { ProgressView().controlSize(.mini) }
            else if let updated = service.lastUpdated {
                Text(updated, style: .time)
            }
        }) {
            VStack(alignment: .leading, spacing: 2) {
                Text(money(service.todayTotal))
                    .font(.system(size: 36, weight: .bold, design: .rounded))
                    .foregroundStyle(accent)
                    .themeGlow(theme, radius: 5)
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.5)
                    .contentTransition(.numericText())
                Text("\(service.todaySales.count) sale\(service.todaySales.count == 1 ? "" : "s") · \(currencyCode)")
                    .font(.system(size: 12))
                    .foregroundStyle(theme.secondaryText)
            }
        }
        .frame(height: 112)
    }

    private var weekCard: some View {
        let peak = max(service.week.map(\.total).max() ?? 0, 0.01)
        let weekTotal = service.week.reduce(0) { $0 + $1.total }
        return Card("7 days", icon: "chart.bar.fill", accessory: { Text(money(weekTotal)) }) {
            HStack(alignment: .bottom, spacing: 6) {
                ForEach(service.week, id: \.day) { entry in
                    let isToday = Calendar.current.isDateInToday(entry.day)
                    VStack(spacing: 4) {
                        GeometryReader { geo in
                            VStack {
                                Spacer(minLength: 0)
                                RoundedRectangle(cornerRadius: 4, style: .continuous)
                                    .fill(isToday ? accent : accent.opacity(0.45))
                                    .frame(height: max(3, geo.size.height * entry.total / peak))
                            }
                        }
                        Text(entry.day.formatted(.dateTime.weekday(.narrow)))
                            .font(.system(size: 9, weight: .semibold))
                            .foregroundStyle(isToday ? theme.text : theme.secondaryText)
                    }
                    .help("\(entry.day.formatted(.dateTime.weekday(.wide))): \(money(entry.total))")
                }
            }
        }
    }

    private var salesCard: some View {
        Card("Sales", icon: "list.bullet", accessory: {
            IconButton(systemName: "arrow.clockwise", size: 20, help: "Refresh") {
                Task { await service.refresh() }
            }
        }) {
            if let error = service.error {
                EmptyState(icon: "exclamationmark.triangle", title: "Couldn't load Stripe", subtitle: error)
            } else if service.lastUpdated == nil {
                ProgressView().controlSize(.small).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if service.todaySales.isEmpty {
                EmptyState(icon: "cart", title: "No sales yet today", subtitle: "New payments show up within a minute.")
            } else {
                ScrollView {
                    LazyVStack(spacing: 5) {
                        ForEach(service.todaySales) { sale in
                            HStack(spacing: 10) {
                                Image(systemName: "dollarsign.circle.fill")
                                    .font(.system(size: 18))
                                    .foregroundStyle(accent)
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(sale.label)
                                        .font(.system(size: 12, weight: .medium))
                                        .foregroundStyle(theme.text)
                                        .lineLimit(1)
                                    Text(sale.created, style: .time)
                                        .font(.system(size: 10))
                                        .foregroundStyle(theme.secondaryText)
                                }
                                Spacer()
                                Text(money(sale.amount))
                                    .font(.system(size: 14, weight: .semibold, design: .rounded))
                                    .monospacedDigit()
                                    .foregroundStyle(theme.text)
                            }
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(theme.text.opacity(0.05)))
                        }
                    }
                }
                .scrollIndicators(.never)
            }
        }
    }
}
