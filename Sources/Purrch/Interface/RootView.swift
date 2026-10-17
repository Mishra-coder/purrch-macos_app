import SwiftUI

struct RootView: View {
    @EnvironmentObject private var state: NotchState
    @EnvironmentObject private var settings: AppSettings

    private let flare: CGFloat = 10

    var body: some View {
        let theme = settings.theme
        let size = state.currentSize
        let expanded = state.isExpanded
        ZStack(alignment: .top) {
            NotchBody(size: size, flare: flare, expanded: expanded,
                      breathing: !expanded && state.liveActivityWing > 0)

            Group {
                if expanded && state.isVideoPeek {
                    VideoPeekView()
                        .transition(.asymmetric(
                            insertion: .opacity.animation(.easeOut(duration: 0.18).delay(0.06)),
                            removal: .opacity.animation(.easeIn(duration: 0.1))
                        ))
                } else if expanded {
                    ExpandedView()
                        .transition(.asymmetric(
                            insertion: .opacity.animation(.easeOut(duration: 0.12)),
                            removal: .opacity.animation(.easeIn(duration: 0.1))
                        ))
                } else {
                    CollapsedView()
                        .transition(.asymmetric(
                            insertion: .opacity.animation(.easeOut(duration: 0.25).delay(0.15)),
                            removal: .opacity.animation(.easeIn(duration: 0.08))
                        ))
                }
            }
            .frame(width: size.width, height: size.height, alignment: .top)
            .clipped()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .environment(\.theme, theme)
        .preferredColorScheme(.dark)
        .animation(Motion.pop, value: state.liveActivityWing)
        .animation(.easeInOut(duration: 0.35), value: settings.themeID)
    }
}

/// The black body of the notch: shape, glow, sheen and shadows.
private struct NotchBody: View {
    @Environment(\.theme) private var theme
    let size: CGSize
    let flare: CGFloat
    let expanded: Bool
    let breathing: Bool

    var body: some View {
        let shape = NotchShape(topRadius: flare, bottomRadius: expanded ? 28 : 10)
        shape
            .fill(theme.background)
            .overlay {
                if expanded {
                    // Soft theme-coloured light falling from the camera, like a lamp in the notch.
                    RadialGradient(colors: [theme.accent.opacity(theme.id == .minimal ? 0.07 : 0.16), .clear],
                                   center: .top, startRadius: 0, endRadius: 360)
                        .clipShape(shape)
                        .transition(.opacity)
                }
            }
            .overlay {
                // Hairline highlight along the lower edge and a theme border when open.
                shape.stroke(
                    LinearGradient(colors: [.clear, expanded ? theme.border.opacity(theme.id == .minimal ? 0.35 : 0.8) : .clear],
                                   startPoint: .top, endPoint: .bottom),
                    lineWidth: 1
                )
            }
            .frame(width: size.width + flare * 2, height: size.height)
            .shadow(color: .black.opacity(expanded ? 0.6 : 0), radius: 24, y: 10)
            .shadow(color: expanded && theme.glow ? theme.accent.opacity(0.28) : .clear, radius: 26)
            .modifier(Breathing(active: breathing, color: theme.accent))
    }
}

/// Slow pulsing glow around the closed notch while something is live inside it.
private struct Breathing: ViewModifier {
    let active: Bool
    let color: Color

    func body(content: Content) -> some View {
        if active {
            content.phaseAnimator([false, true]) { view, lit in
                view.shadow(color: color.opacity(lit ? 0.55 : 0.1), radius: lit ? 12 : 4, y: 2)
            } animation: { _ in .easeInOut(duration: 1.6) }
        } else {
            content
        }
    }
}

// MARK: - Expanded

struct ExpandedView: View {
    @EnvironmentObject private var state: NotchState
    @EnvironmentObject private var settings: AppSettings
    @Environment(\.theme) private var theme
    /// Drives the staggered entrance: header, then content, then the tab bar.
    @State private var shown = false

    var body: some View {
        ZStack {
            if state.theaterVideoID != nil {
                VideoTheaterView()
                    .transition(.opacity.combined(with: .scale(scale: 0.92)))
                    .zIndex(1)
            } else {
                panel
                    .transition(.opacity)
            }
        }
        .onAppear {
            withAnimation(Motion.expoOut(0.6).delay(0.07)) { shown = true }
        }
    }

    private var panel: some View {
        VStack(spacing: 0) {
            header
                .frame(height: max(state.notchSize.height, 30))
                .padding(.horizontal, 18)
                .padding(.top, 2)
                .opacity(shown ? 1 : 0)
                .offset(y: shown ? 0 : -8)

            ZStack {
                content
                    .id(contentKey)
                    .transition(.asymmetric(
                        insertion: .modifier(active: SlideBlur(x: 34 * state.switchDirection, blur: 8, opacity: 0, scale: 0.98),
                                             identity: SlideBlur()),
                        removal: .modifier(active: SlideBlur(x: 0, blur: 6, opacity: 0, scale: 0.98),
                                           identity: SlideBlur())
                    ))
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .environment(\.moduleTint, state.showSettings ? theme.accent : theme.accent(for: state.selected))
            .environment(\.theme, state.showSettings ? theme : theme.tinted(for: state.selected))
            .padding(.horizontal, 16)
            .padding(.top, 6)
            .padding(.bottom, 10)
            .opacity(shown ? 1 : 0)
            .scaleEffect(shown ? 1 : 0.965, anchor: .top)
            .blur(radius: shown ? 0 : 10)

            if settings.hasCompletedOnboarding {
                TabBar(shown: shown)
                    .padding(.horizontal, 12)
                    .padding(.bottom, 12)
            }
        }
        .foregroundStyle(theme.text)
    }

    private var contentKey: String {
        if !settings.hasCompletedOnboarding { return "onboarding" }
        return state.showSettings ? "settings" : state.selected.rawValue
    }

    @ViewBuilder
    private var content: some View {
        if !settings.hasCompletedOnboarding {
            OnboardingView()
        } else if state.showSettings {
            SettingsView()
        } else {
            state.selected.view
        }
    }

    private var header: some View {
        let module = state.selected
        let tint = state.showSettings ? theme.accent : theme.accent(for: module)
        return HStack(spacing: 10) {
            Image(systemName: state.showSettings ? "gearshape.fill" : module.icon)
                .font(.system(size: 12, weight: .bold))
                .foregroundStyle(theme.glow ? theme.background : tint)
                .contentTransition(.symbolEffect(.replace))
                .frame(width: 26, height: 26)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(theme.glow ? AnyShapeStyle(tint) : AnyShapeStyle(tint.opacity(0.18))))
                .themeGlow(theme, radius: 5)
            VStack(alignment: .leading, spacing: 0) {
                Text(state.showSettings ? "Settings" : module.title)
                    .font(.system(size: 13.5, weight: .bold))
                    .contentTransition(.interpolate)
                Group {
                    if state.showSettings {
                        Text("Themes, modules, keys and permissions")
                    } else if module == .home {
                        Text(Date.now, format: .dateTime.weekday(.wide).month(.wide).day())
                    } else {
                        Text(module.subtitle)
                    }
                }
                .font(.system(size: 10.5))
                .foregroundStyle(theme.secondaryText)
                .lineLimit(1)
                .contentTransition(.opacity)
            }
            .frame(maxWidth: 220, alignment: .leading)
            Spacer(minLength: 0)
            // Leave the camera housing empty.
            Color.clear.frame(width: state.hasPhysicalNotch ? state.notchSize.width - 40 : 0)
            Spacer(minLength: 0)
            controls
        }
    }

    /// Pin and settings, grouped in one capsule.
    private var controls: some View {
        HStack(spacing: 2) {
            // Pinned is the one state that stops the panel closing on hover-out, so make it unmistakable.
            Button {
                withAnimation(Motion.pop) { state.isPinned.toggle() }
            } label: {
                HStack(spacing: 5) {
                    Image(systemName: state.isPinned ? "pin.fill" : "pin")
                        .rotationEffect(.degrees(state.isPinned ? 0 : 40))
                        .symbolEffect(.bounce, value: state.isPinned)
                    if state.isPinned {
                        Text("Pinned").transition(.opacity.combined(with: .move(edge: .leading)))
                    }
                }
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(state.isPinned ? theme.background : theme.text.opacity(0.85))
                .padding(.horizontal, state.isPinned ? 10 : 0)
                .frame(minWidth: 26, minHeight: 24)
                .background(Capsule().fill(state.isPinned ? theme.accent : .clear))
                .contentShape(Capsule())
            }
            .buttonStyle(.plain)
            .help(state.isPinned ? "Pinned: stays open. Click to let it close when you move away." : "Keep open")

            Button {
                state.switchDirection = 1
                withAnimation(Motion.expoOut(0.45)) { state.showSettings.toggle() }
            } label: {
                Image(systemName: state.showSettings ? "xmark" : "gearshape")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(theme.text.opacity(0.85))
                    .rotationEffect(.degrees(state.showSettings ? 90 : 0))
                    .frame(width: 26, height: 24)
                    .contentShape(Capsule())
            }
            .buttonStyle(.plain)
            .help("Settings")
        }
        .padding(2)
        .background(Capsule().fill(theme.text.opacity(0.08)))
        .overlay(Capsule().strokeBorder(theme.text.opacity(0.08), lineWidth: 0.6))
    }
}

/// Offset + blur + fade, used to slide module content in and out.
struct SlideBlur: ViewModifier {
    var x: CGFloat = 0
    var blur: CGFloat = 0
    var opacity: Double = 1
    var scale: CGFloat = 1

    func body(content: Content) -> some View {
        content
            .offset(x: x)
            .scaleEffect(scale)
            .blur(radius: blur)
            .opacity(opacity)
    }
}

/// The module dock. Icons magnify under the pointer like the macOS Dock and show their name;
/// drag one sideways to reorder, the others slide out of the way.
struct TabBar: View {
    @EnvironmentObject private var state: NotchState
    @EnvironmentObject private var settings: AppSettings
    @Environment(\.theme) private var theme
    @Namespace private var selection
    var shown = true

    /// Live order while a drag is in progress; nil otherwise.
    @State private var draftOrder: [ModuleID]?
    @State private var dragged: ModuleID?
    @State private var dragStartIndex = 0
    @State private var dragX: CGFloat = 0
    @State private var hovered: Int?

    private let slot: CGFloat = 31

    var body: some View {
        let modules = draftOrder ?? settings.enabledModules
        HStack(spacing: 0) {
            ForEach(Array(modules.enumerated()), id: \.element) { index, module in
                let isDragged = dragged == module
                TabButton(module: module,
                          selected: state.selected == module && !state.showSettings,
                          namespace: selection,
                          lifted: isDragged,
                          magnification: magnification(at: index),
                          showsLabel: hovered == index && dragged == nil)
                .frame(width: slot, height: 34)
                .offset(x: isDragged ? dragX - CGFloat(index - dragStartIndex) * slot : 0)
                .zIndex(isDragged ? 3 : (hovered == index ? 2 : 0))
                .onHover { inside in
                    withAnimation(Motion.peek) {
                        if inside { hovered = index } else if hovered == index { hovered = nil }
                    }
                }
                // Drag wins once the pointer moves; a plain click falls through to the tap.
                .gesture(
                    reorderGesture(for: module, in: modules)
                        .exclusively(before: TapGesture().onEnded { state.select(module) })
                )
                .opacity(shown ? 1 : 0)
                .offset(y: shown ? 0 : 18)
                .animation(Motion.open.delay(0.1 + Double(index) * 0.012), value: shown)
            }
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 3)
        .background(CardSurface(radius: 16))
        .frame(maxWidth: .infinity)
        .opacity(shown ? 1 : 0)
    }

    /// Dock-style magnification: the hovered icon grows most, its neighbours a little.
    private func magnification(at index: Int) -> CGFloat {
        guard let hovered, dragged == nil else { return 1 }
        switch abs(index - hovered) {
        case 0: return 1.3
        case 1: return 1.12
        case 2: return 1.04
        default: return 1
        }
    }

    private func reorderGesture(for module: ModuleID, in modules: [ModuleID]) -> some Gesture {
        DragGesture(minimumDistance: 5, coordinateSpace: .global)
            .onChanged { value in
                if dragged == nil {
                    draftOrder = modules
                    dragStartIndex = modules.firstIndex(of: module) ?? 0
                    withAnimation(Motion.pop) { dragged = module; hovered = nil }
                }
                guard var order = draftOrder, let current = order.firstIndex(of: module) else { return }
                dragX = value.translation.width
                let target = min(max(dragStartIndex + Int((dragX / slot).rounded()), 0), order.count - 1)
                if target != current {
                    withAnimation(Motion.pop) {
                        order.move(fromOffsets: IndexSet(integer: current), toOffset: target > current ? target + 1 : target)
                        draftOrder = order
                    }
                }
            }
            .onEnded { _ in
                if let order = draftOrder { settings.reorderEnabled(order) }
                withAnimation(Motion.pop) {
                    dragged = nil
                    dragX = 0
                    draftOrder = nil
                }
            }
    }
}

private struct TabButton: View {
    @Environment(\.theme) private var theme
    let module: ModuleID
    let selected: Bool
    let namespace: Namespace.ID
    var lifted = false
    var magnification: CGFloat = 1
    var showsLabel = false
    @State private var bounces = 0

    var body: some View {
        let tint = theme.accent(for: module)
        let scale = lifted ? 1.25 : magnification
        ZStack {
            if selected {
                RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .fill(theme.glow ? tint : tint.opacity(0.2))
                    .shadow(color: tint.opacity(theme.glow ? 0.7 : 0.35), radius: 8)
                    .frame(width: 28, height: 26)
                    .matchedGeometryEffect(id: "selection", in: namespace)
            }
            if lifted {
                RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .fill(theme.surface)
                    .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous)
                        .strokeBorder(tint.opacity(0.7), lineWidth: 1))
                    .shadow(color: .black.opacity(0.5), radius: 8, y: 4)
                    .frame(width: 28, height: 26)
            }
            Image(systemName: module.icon)
                .font(.system(size: 13.5, weight: .medium))
                .foregroundStyle(selected ? (theme.glow ? theme.background : tint)
                                          : theme.text.opacity(magnification > 1.1 ? 1 : 0.68))
                .symbolEffect(.bounce, value: bounces)
        }
        .overlay(alignment: .bottom) {
            // Running indicator, like the Dock.
            Circle()
                .fill(tint)
                .frame(width: 3.5, height: 3.5)
                .offset(y: 3)
                .opacity(selected ? 1 : 0)
        }
        .scaleEffect(scale, anchor: .bottom)
        .offset(y: lifted ? -6 : -(scale - 1) * 10)
        .overlay(alignment: .top) {
            if showsLabel {
                Text(module.title)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(theme.text)
                    .fixedSize()
                    .padding(.horizontal, 9)
                    .padding(.vertical, 4)
                    .background(Capsule().fill(theme.surface).shadow(color: .black.opacity(0.45), radius: 6, y: 3))
                    .overlay(Capsule().strokeBorder(theme.text.opacity(0.12), lineWidth: 0.6))
                    .offset(y: -34)
                    .transition(.opacity.combined(with: .scale(scale: 0.85, anchor: .bottom)))
                    .allowsHitTesting(false)
            }
        }
        .contentShape(Rectangle())
        .onChange(of: selected) { _, isSelected in
            if isSelected { bounces += 1 }
        }
    }
}
